package instancefile

import (
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"
)

func write(t *testing.T, dir, name, body string) string {
	t.Helper()
	p := filepath.Join(dir, name)
	if err := os.WriteFile(p, []byte(body), 0o600); err != nil {
		t.Fatal(err)
	}
	return p
}

func valid(pid int) string {
	return `{"schema":1,"instance_id":"i-` + itoa(pid) + `","pid":` + itoa(pid) + `,"port":50000,"token":"t","rhino_version":"8.35","platform":"macos","bridge_version":"dev","schema_fingerprint":"","started_at":"2026-09-10T00:00:00Z"}`
}

func itoa(i int) string { return strconv.Itoa(i) }

func TestScanMissingDirIsEmptyNotError(t *testing.T) {
	res, err := Scan(filepath.Join(t.TempDir(), "nope"), func(int) bool { return true })
	if err != nil || len(res.Live) != 0 {
		t.Fatalf("got %+v, %v", res, err)
	}
}

func TestScanListsLiveAndDeletesDead(t *testing.T) {
	dir := t.TempDir()
	write(t, dir, "1.json", valid(1))
	dead := write(t, dir, "2.json", valid(2))
	alive := func(pid int) bool { return pid == 1 }
	res, err := Scan(dir, alive)
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Live) != 1 || res.Live[0].PID != 1 || res.Live[0].Path != filepath.Join(dir, "1.json") {
		t.Fatalf("live = %+v", res.Live)
	}
	if len(res.Removed) != 1 || res.Removed[0] != dead {
		t.Fatalf("removed = %v", res.Removed)
	}
	if _, err := os.Stat(dead); !os.IsNotExist(err) {
		t.Fatal("dead file should be deleted")
	}
}

func TestScanSkipsMalformedButLeavesThem(t *testing.T) {
	dir := t.TempDir()
	write(t, dir, "3.json", "{not json")
	write(t, dir, "notes.txt", "ignored")
	write(t, dir, "abc.json", valid(4))                                         // not named <pid>.json
	write(t, dir, "5.json", strings.Replace(valid(5), `"pid":5`, `"pid":6`, 1)) // name/body mismatch
	write(t, dir, "7.json", strings.Replace(valid(7), `"schema":1`, `"schema":0`, 1))
	res, err := Scan(dir, func(int) bool { return true })
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Live) != 0 {
		t.Fatalf("live = %+v", res.Live)
	}
	if len(res.Skipped) != 4 {
		t.Fatalf("skipped = %v", res.Skipped)
	}
	if _, err := os.Stat(filepath.Join(dir, "3.json")); err != nil {
		t.Fatal("malformed files are left in place for a human to look at")
	}
}

func TestValidateRejectsEachMissingField(t *testing.T) {
	cases := map[string]File{
		"schema":    {Schema: 0, InstanceID: "x", PID: 1, Port: 1, Token: "t"},
		"instance":  {Schema: 1, InstanceID: "", PID: 1, Port: 1, Token: "t"},
		"pid":       {Schema: 1, InstanceID: "x", PID: 0, Port: 1, Token: "t"},
		"port-low":  {Schema: 1, InstanceID: "x", PID: 1, Port: 0, Token: "t"},
		"port-high": {Schema: 1, InstanceID: "x", PID: 1, Port: 70000, Token: "t"},
		"token":     {Schema: 1, InstanceID: "x", PID: 1, Port: 1, Token: ""},
	}
	for name, f := range cases {
		if f.Validate() == nil {
			t.Errorf("%s: expected an error", name)
		}
	}
	ok := File{Schema: 1, InstanceID: "x", PID: 1, Port: 1, Token: "t"}
	if err := ok.Validate(); err != nil {
		t.Fatal(err)
	}
}

func TestNewerSchemaIsStillRead(t *testing.T) {
	dir := t.TempDir()
	p := write(t, dir, "8.json", strings.Replace(valid(8), `"schema":1`, `"schema":2,"future_field":true`, 1))
	f, err := Read(p)
	if err != nil || f.Schema != 2 {
		t.Fatalf("got %+v, %v", f, err)
	}
}

func TestProcessAliveOnOurselves(t *testing.T) {
	if !ProcessAlive(os.Getpid()) {
		t.Fatal("this process is alive")
	}
	// A pid no process can have on any OS (max pid is far below this on macOS/Linux; Windows pids are multiples of 4 under 2^32 but this one is unused in practice).
	if ProcessAlive(2_000_000_000) {
		t.Fatal("pid 2000000000 should not be alive")
	}
	_ = time.Now
}
