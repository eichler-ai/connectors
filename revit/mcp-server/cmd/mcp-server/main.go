// Command mcp-server is the Revit MCP Server (the broker), per PRD §04/§05:
// a single process that speaks MCP over stdio to Claude/agents, and TCP/
// NDJSON to one or more Revit MCP Bridge (add-in) instances.
//
// Because the broker is stdio-spawned per MCP client (PRD §05 "Broker
// singleton & port contention"), every invocation first races for an
// exclusive lock file. The winner becomes primary: it binds the TCP port,
// mints a fresh auth token, writes broker.json, and runs the real MCP
// server. Everyone else becomes secondary: it reads the primary's
// broker.json and transparently pipes its own stdio MCP traffic through a
// TCP connection to the primary instead.
package main

import (
	"bufio"
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"os"
	"os/signal"
	"path/filepath"
	"strconv"
	"syscall"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/broker"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/execution"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/mcpserver"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/registry"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/singleton"
	"github.com/eichler-ai/connectors/revit/mcp-server/internal/transport"
)

const serverName = "revit-mcp-server"

// version is overridable at build time via -ldflags.
var version = "dev"

func main() {
	mode := flag.String("mode", envOr("REVIT_MCP_MODE", "local"), "connection topology: \"local\" (127.0.0.1 only, default) or \"remote\" (bind a configured non-loopback interface) — PRD §05")
	bindAddr := flag.String("bind", envOr("REVIT_MCP_BIND", ""), "non-loopback bind address, required when -mode=remote (e.g. the Parallels shared-network host adapter address)")
	port := flag.Int("port", envIntOr("REVIT_MCP_PORT", 0), "TCP port for the add-in-facing listener; 0 picks an ephemeral port (discovered via broker.json)")
	appDataDir := flag.String("app-data-dir", os.Getenv("REVIT_MCP_APPDATA"), "override the platform app-data directory (mainly for tests/dev); defaults to the PRD §09 convention")
	flag.Parse()

	logger := log.New(os.Stderr, "["+serverName+"] ", log.LstdFlags|log.Lmsgprefix)

	if err := run(*mode, *bindAddr, *port, *appDataDir, logger); err != nil {
		logger.Fatalf("fatal: %v", err)
	}
}

func envOr(key, def string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return def
}

func envIntOr(key string, def int) int {
	if v := os.Getenv(key); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			return n
		}
	}
	return def
}

func run(mode, bindAddr string, port int, appDataDirOverride string, logger *log.Logger) error {
	if mode != "local" && mode != "remote" {
		return fmt.Errorf("invalid -mode %q: must be \"local\" or \"remote\"", mode)
	}
	if mode == "remote" && bindAddr == "" {
		return fmt.Errorf("-bind is required in remote mode (PRD §05: a specific configured non-loopback interface, never 0.0.0.0)")
	}
	if mode == "local" {
		bindAddr = "127.0.0.1"
	}

	dataDir := appDataDirOverride
	if dataDir == "" {
		d, err := singleton.AppDataDir()
		if err != nil {
			return fmt.Errorf("resolving app-data directory: %w", err)
		}
		dataDir = d
	}
	if err := os.MkdirAll(dataDir, 0o755); err != nil {
		return fmt.Errorf("creating app-data directory %q: %w", dataDir, err)
	}

	lockPath := filepath.Join(dataDir, "broker.lock")
	lock, primary, err := singleton.AcquireLock(lockPath)
	if err != nil {
		return fmt.Errorf("acquiring singleton lock: %w", err)
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	if primary {
		defer lock.Release()
		return runPrimary(ctx, bindAddr, port, dataDir, logger)
	}
	return runSecondary(ctx, dataDir, logger)
}

func runPrimary(ctx context.Context, bindAddr string, port int, dataDir string, logger *log.Logger) error {
	ln, err := net.Listen("tcp", net.JoinHostPort(bindAddr, strconv.Itoa(port)))
	if err != nil {
		return fmt.Errorf("binding TCP listener on %s:%d: %w", bindAddr, port, err)
	}
	defer ln.Close()

	tcpAddr, ok := ln.Addr().(*net.TCPAddr)
	if !ok {
		return fmt.Errorf("unexpected listener address type %T", ln.Addr())
	}

	token, err := singleton.GenerateToken()
	if err != nil {
		return fmt.Errorf("generating auth token: %w", err)
	}

	info := singleton.BrokerInfo{
		Host:      bindAddr,
		Port:      tcpAddr.Port,
		PID:       os.Getpid(),
		StartedAt: time.Now().UTC(),
		Token:     token,
	}
	if err := singleton.WriteBrokerJSON(dataDir, info); err != nil {
		return fmt.Errorf("writing broker.json: %w", err)
	}
	logger.Printf("primary: listening on %s:%d, broker.json written to %s", bindAddr, tcpAddr.Port, dataDir)

	mcpServer := mcp.NewServer(&mcp.Implementation{Name: serverName, Version: version}, nil)
	execMgr := execution.NewManager()
	mcpserver.Register(mcpServer, execMgr)

	b := &broker.Broker{
		Token:     token,
		Registry:  registry.New(),
		Execution: execMgr,
		MCPServer: mcpServer,
		Logger:    logger,
	}

	serveErr := make(chan error, 1)
	go func() { serveErr <- b.Serve(ctx, ln) }()

	// The primary's own MCP session runs over its own stdio, exactly like
	// every secondary's proxied session runs over TCP (PRD §05: "From the
	// agent's point of view behavior is identical regardless of which
	// broker process it happens to be talking to").
	err = mcpServer.Run(ctx, &mcp.StdioTransport{})
	stop := ctx.Err() != nil
	if err != nil && !stop {
		return fmt.Errorf("stdio MCP session ended: %w", err)
	}
	return nil
}

func runSecondary(ctx context.Context, dataDir string, logger *log.Logger) error {
	var info singleton.BrokerInfo
	var err error
	// The primary listens before anything else (PRD §05), but there's still
	// a brief window between winning the lock and finishing the
	// broker.json write; retry briefly rather than failing outright.
	deadline := time.Now().Add(5 * time.Second)
	for {
		info, err = singleton.ReadBrokerJSON(dataDir)
		if err == nil {
			break
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("reading broker.json from %q after waiting for the primary: %w", dataDir, err)
		}
		time.Sleep(100 * time.Millisecond)
	}

	conn, err := net.Dial("tcp", net.JoinHostPort(info.Host, strconv.Itoa(info.Port)))
	if err != nil {
		return fmt.Errorf("dialing primary broker at %s:%d: %w", info.Host, info.Port, err)
	}
	defer conn.Close()
	logger.Printf("secondary: proxying stdio through primary at %s:%d", info.Host, info.Port)

	authReq, err := transport.NewRequest(json.RawMessage(`"auth"`), "auth", map[string]any{
		"token": info.Token,
		"role":  string(broker.RoleAgentClient),
	})
	if err != nil {
		return fmt.Errorf("building auth request: %w", err)
	}
	b, err := json.Marshal(authReq)
	if err != nil {
		return fmt.Errorf("encoding auth request: %w", err)
	}
	if _, err := conn.Write(append(b, '\n')); err != nil {
		return fmt.Errorf("sending auth request to primary: %w", err)
	}

	br := bufio.NewReader(conn)
	line, err := br.ReadBytes('\n')
	if err != nil {
		return fmt.Errorf("reading auth response from primary: %w", err)
	}
	var resp transport.Message
	if err := json.Unmarshal(line, &resp); err != nil {
		return fmt.Errorf("decoding auth response from primary: %w", err)
	}
	if resp.Error != nil {
		return fmt.Errorf("primary rejected this secondary's auth: %s", resp.Error.Message)
	}

	// From here, transparently pipe stdin -> conn and conn -> stdout. The
	// MCP stdio protocol is itself NDJSON, matching the wire framing we
	// just used for auth (PRD §05 "Framing"), so no re-encoding is needed —
	// this process is a pure byte-level proxy of its own stdio traffic.
	// Either direction closing ends the proxy: the MCP client closes stdin
	// as its normal stdio-subprocess shutdown signal (it isn't waiting on
	// further responses once it does), and the primary closing its end
	// means there's nothing left to proxy either way.
	errCh := make(chan error, 2)
	go func() {
		_, err := io.Copy(conn, os.Stdin)
		errCh <- err
	}()
	go func() {
		_, err := io.Copy(os.Stdout, br)
		errCh <- err
	}()

	select {
	case <-ctx.Done():
		return nil
	case err := <-errCh:
		return err
	}
}
