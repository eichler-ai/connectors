// Command mcp-server is the Rhino MCP Server (rhino/docs/PRD.md §04/§05): one
// process per MCP client, speaking MCP over stdio and dialling in to every
// running Rhino MCP Bridge. There is no singleton and no primary: each server
// process is independent, and the plug-in owns every piece of state that has
// to be shared (busy state, the result ring buffer).
package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/google/uuid"
	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/internal/servercore/buildinfo"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/appdata"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/dialer"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/execution"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/mcpserver"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/registry"
)

const serverName = "rhino-mcp-server"

// version identifies a RELEASE; "dev" for every local and CI build. The source
// revision comes from buildinfo with no flags (Revit issue #116).
var version = "dev"

func versionLine() string { return version + " (" + buildinfo.Read().Summary() + ")" }

func main() {
	appDataDir := flag.String("app-data-dir", os.Getenv("RHINO_MCP_APPDATA"), "override the connector's app-data root (instances/ is scanned under it); defaults to the platform directory, PRD §05")
	showVersion := flag.Bool("version", false, "print this binary's version and source revision, then exit")
	flag.Parse()

	if *showVersion {
		fmt.Println(serverName + " " + versionLine())
		return
	}

	logger := log.New(os.Stderr, "["+serverName+"] ", log.LstdFlags|log.Lmsgprefix)
	logger.Printf("starting %s", versionLine())
	if err := run(*appDataDir, logger); err != nil {
		logger.Fatalf("fatal: %v", err)
	}
}

func run(appDataDir string, logger *log.Logger) error {
	root := appDataDir
	if root == "" {
		var err error
		if root, err = appdata.ConnectorRoot(); err != nil {
			return fmt.Errorf("resolving the app-data directory: %w", err)
		}
	}
	instancesDir := appdata.InstancesDir(root)
	if err := os.MkdirAll(instancesDir, 0o755); err != nil {
		return fmt.Errorf("creating %s: %w", instancesDir, err)
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	serverID := serverName + "-" + uuid.NewString()[:8]
	reg := registry.New()
	dial := dialer.New(dialer.Options{
		InstancesDir:  instancesDir,
		ServerID:      serverID,
		ServerVersion: versionLine(),
		Registry:      reg,
		Logf:          logger.Printf,
	})
	go dial.Run(ctx)
	logger.Printf("scanning %s as %s", instancesDir, serverID)

	// Heartbeat prune (PRD §05): a silent instance leaves the registry and its socket is closed,
	// so recovery goes through the normal redial instead of a lingering half-open connection.
	go func() {
		t := time.NewTicker(30 * time.Second)
		defer t.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-t.C:
				for id, epoch := range reg.PruneStale(time.Now()) {
					logger.Printf("registry: pruned silent instance %s", id)
					dial.CloseInstance(id, epoch)
				}
			}
		}
	}()

	s := mcp.NewServer(&mcp.Implementation{Name: serverName, Version: versionLine()}, nil)
	mcpserver.RegisterInstances(s, reg, nil, nil)
	router := execution.NewRouter(dial, serverID)
	mcpserver.RegisterExecution(s, router)
	mcpserver.RegisterCapture(s, router)

	err := s.Run(ctx, &mcp.StdioTransport{})
	if err != nil && ctx.Err() == nil {
		return fmt.Errorf("stdio MCP session ended: %w", err)
	}
	return nil
}
