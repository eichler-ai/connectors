//go:build harness

// Package harness_test is the Rhino connector's tier-2 suite (implementation-plan.md): real server
// binary, real MCP over stdio, real Rhino. It never launches or closes Rhino; every case that needs
// one SKIPs cleanly if none is connected. Run natively on the Mac:
//
//	cd rhino/mcp-server && go build -o mcp-server-mac ./cmd/mcp-server
//	cd ../test-harness && go test -tags harness ./... -v -broker-exe ../mcp-server/mcp-server-mac
//
// Findings from the phase 1a spikes that shape this file: a locked screen stops Rhino from
// running scripts and stops GUI automation, so preflight checks it; Rhino reports by file, never
// by stdout; assert on names and ids, never on counts alone.
package harness_test

import (
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"os/exec"
	"runtime"
	"strings"
	"testing"
	"time"

	"github.com/eichler-ai/connectors/rhino/test-harness/mcpclient"
)

var (
	serverExe  = flag.String("broker-exe", os.Getenv("MCP_SERVER_EXE"), "path to the built rhino mcp-server binary under test")
	appDataDir = flag.String("app-data-dir", os.Getenv("MCP_SERVER_APPDATA"), "optional -app-data-dir for the server; default is the platform directory the real plug-in writes to")
)

type document struct {
	DocumentID string `json:"document_id"`
	Title      string `json:"title"`
	Path       string `json:"path"`
	Active     bool   `json:"active"`
}

type instance struct {
	InstanceID    string     `json:"instance_id"`
	RhinoVersion  string     `json:"rhino_version"`
	Platform      string     `json:"platform"`
	BridgeVersion string     `json:"bridge_version"`
	PID           int        `json:"pid"`
	Status        string     `json:"status"`
	Documents     []document `json:"documents"`
}

type listInstancesOut struct {
	Instances []instance `json:"instances"`
	Guidance  string     `json:"guidance"`
}

// preflight refuses to run a live case in a state where every step would be a silent no-op.
func preflight(t *testing.T) {
	t.Helper()
	if *serverExe == "" {
		t.Skip("no -broker-exe / MCP_SERVER_EXE set; nothing to test against")
	}
	if runtime.GOOS == "darwin" {
		out, err := exec.Command("sh", "-c", `ioreg -n Root -d1 -a 2>/dev/null | grep -A1 CGSSessionScreenIsLocked | tail -1`).Output()
		if err == nil && strings.Contains(string(out), "<true/>") {
			t.Fatal("the screen is locked: Rhino does not run scripts and GUI automation is blocked (spikes/phase-1a-findings.md §6); unlock and rerun")
		}
	}
}

func startServer(t *testing.T) *mcpclient.Client {
	t.Helper()
	preflight(t)
	var args []string
	if *appDataDir != "" {
		args = append(args, "-app-data-dir", *appDataDir)
	}
	c, err := mcpclient.Start(*serverExe, args...)
	if err != nil {
		t.Fatalf("starting the server: %v", err)
	}
	t.Cleanup(func() { c.Close() })
	return c
}

// toolResult is the MCP tools/call envelope: content (text, the readable form) plus
// structuredContent on success. isError must be checked before decoding structuredContent,
// which is absent on error (the Revit harness's lesson).
type toolResult struct {
	Content []struct {
		Type string `json:"type"`
		Text string `json:"text"`
	} `json:"content"`
	StructuredContent json.RawMessage `json:"structuredContent"`
	IsError           bool            `json:"isError"`
}

func decodeToolResult[T any](t *testing.T, raw json.RawMessage) T {
	t.Helper()
	var tr toolResult
	if err := json.Unmarshal(raw, &tr); err != nil {
		t.Fatalf("decode tool envelope: %v\nraw: %s", err, raw)
	}
	if tr.IsError {
		text := "(no content)"
		if len(tr.Content) > 0 {
			text = tr.Content[0].Text
		}
		t.Fatalf("tool call returned an error: %s", text)
	}
	var out T
	if err := json.Unmarshal(tr.StructuredContent, &out); err != nil {
		t.Fatalf("decode structuredContent: %v\nraw: %s", err, tr.StructuredContent)
	}
	return out
}

func listInstances(t *testing.T, c *mcpclient.Client) listInstancesOut {
	t.Helper()
	raw, err := c.CallTool("list_instances", map[string]any{}, 10*time.Second)
	if err != nil {
		t.Fatalf("list_instances: %v", err)
	}
	return decodeToolResult[listInstancesOut](t, raw)
}

// waitForInstance polls until the dialer has attached at least one Rhino (a few scans), then
// returns it; skips, never fails, when none appears, per the harness rule.
func waitForInstance(t *testing.T, c *mcpclient.Client) instance {
	t.Helper()
	deadline := time.Now().Add(15 * time.Second)
	for {
		out := listInstances(t, c)
		if len(out.Instances) > 0 {
			return out.Instances[0]
		}
		if time.Now().After(deadline) {
			t.Skipf("no Rhino instance connected (guidance: %s)", out.Guidance)
		}
		time.Sleep(500 * time.Millisecond)
	}
}

func TestListInstancesShowsTheRunningRhino(t *testing.T) {
	c := startServer(t)
	inst := waitForInstance(t, c)
	if inst.InstanceID == "" || inst.PID == 0 || inst.RhinoVersion == "" || inst.Platform == "" || inst.BridgeVersion == "" {
		t.Fatalf("instance is missing identity fields: %+v", inst)
	}
	if inst.Status != "idle" {
		t.Fatalf("a fresh instance should be idle, got %q", inst.Status)
	}
	if len(inst.Documents) == 0 {
		t.Fatal("expected at least one open document (the harness assumes a model is open)")
	}
	var active int
	for _, d := range inst.Documents {
		if d.DocumentID == "" || d.Title == "" {
			t.Fatalf("document missing id/title: %+v", d)
		}
		if !strings.HasPrefix(d.DocumentID, "doc-") && !strings.HasPrefix(d.DocumentID, "tmp-") {
			t.Fatalf("document id %q has neither prefix (PRD §12)", d.DocumentID)
		}
		if (d.Path == "") != strings.HasPrefix(d.DocumentID, "tmp-") {
			t.Fatalf("path/prefix disagree: %+v", d)
		}
		if d.Active {
			active++
		}
	}
	if active != 1 {
		t.Fatalf("exactly one document should be active, got %d", active)
	}
	t.Logf("instance %s pid %d Rhino %s (%s, bridge %s), %d document(s)", inst.InstanceID, inst.PID, inst.RhinoVersion, inst.Platform, inst.BridgeVersion, len(inst.Documents))
}

func TestTwoServersSeeTheSameInstance(t *testing.T) {
	// PRD §05: two Claude sessions are two independent servers dialled into one plug-in.
	a := startServer(t)
	instA := waitForInstance(t, a)
	b := startServer(t)
	instB := waitForInstance(t, b)
	if instA.InstanceID != instB.InstanceID {
		t.Fatalf("servers disagree: %s vs %s", instA.InstanceID, instB.InstanceID)
	}
	// Closing one must not disturb the other.
	b.Close()
	time.Sleep(500 * time.Millisecond)
	again := waitForInstance(t, a)
	if again.InstanceID != instA.InstanceID {
		t.Fatalf("server A lost its instance when B closed")
	}
}

func TestServerRestartFindsTheInstanceAgain(t *testing.T) {
	a := startServer(t)
	first := waitForInstance(t, a)
	a.Close()
	b := startServer(t)
	second := waitForInstance(t, b)
	if first.InstanceID != second.InstanceID {
		t.Fatalf("instance id changed across a server restart: %s -> %s (the plug-in mints it once per process)", first.InstanceID, second.InstanceID)
	}
}

func TestDocumentEventsRefreshTheRegistry(t *testing.T) {
	// A new document opened in Rhino appears in list_instances without a reconnect (PRD §05 live
	// snapshot). Drives Rhino through the rhinocode CLI (spikes §6), macOS only for now.
	if runtime.GOOS != "darwin" {
		t.Skip("uses the macOS rhinocode CLI")
	}
	rc := "/Applications/Rhino 8.app/Contents/Resources/bin/rhinocode"
	if _, err := os.Stat(rc); err != nil {
		t.Skip("rhinocode CLI not found")
	}
	c := startServer(t)
	before := waitForInstance(t, c)

	// Open a second document via a script: New on the Mac creates another window/document.
	script := fmt.Sprintf("#! python 3\nimport Rhino\nRhino.RhinoDoc.Create(None)\n")
	path := fmt.Sprintf("%s/harness-new-doc-%d.py", t.TempDir(), time.Now().UnixNano())
	if err := os.WriteFile(path, []byte(script), 0o600); err != nil {
		t.Fatal(err)
	}
	if out, err := exec.Command(rc, "script", path).CombinedOutput(); err != nil {
		t.Fatalf("rhinocode script: %v\n%s", err, out)
	}
	t.Cleanup(func() {
		// Close the document we created: the newest untitled one. Best effort.
		closeScript := "#! python 3\nimport Rhino\ndocs = list(Rhino.RhinoDoc.OpenDocuments())\nif len(docs) > 1:\n    d = docs[-1]\n    d.Modified = False\n    Rhino.RhinoApp.InvokeOnUiThread(lambda: None)\n"
		p := path + ".close.py"
		os.WriteFile(p, []byte(closeScript), 0o600)
		exec.Command(rc, "script", p).Run()
	})

	deadline := time.Now().Add(10 * time.Second)
	for {
		now := waitForInstance(t, c)
		if len(now.Documents) > len(before.Documents) {
			t.Logf("documents %d -> %d after RhinoDoc.Create", len(before.Documents), len(now.Documents))
			return
		}
		if time.Now().After(deadline) {
			t.Fatalf("new document never appeared: before %d, now %d", len(before.Documents), len(now.Documents))
		}
		time.Sleep(300 * time.Millisecond)
	}
}
