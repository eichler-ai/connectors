package yak

import (
	"context"
	"errors"
	"reflect"
	"testing"
)

func TestParsePackageLines(t *testing.T) {
	in := "LunchBox (2025.5.5.0)\n\nPufferfish (3.0.0)\nKaroroCAM (1.2609.10+19753)\n"
	got := parsePackageLines(in)
	want := []Package{
		{"LunchBox", "2025.5.5.0"},
		{"Pufferfish", "3.0.0"},
		{"KaroroCAM", "1.2609.10+19753"},
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("parsePackageLines = %#v, want %#v", got, want)
	}
}

func TestParsePackageLines_Empty(t *testing.T) {
	if got := parsePackageLines("\n  \n"); got != nil {
		t.Fatalf("expected nil for no matches, got %#v", got)
	}
}

func TestParseList(t *testing.T) {
	in := "Package directory: /Users/x/Library/Application Support/McNeel/Rhinoceros/packages/8.0\n\nrhino-mcp-bridge (0.0.1)\nPufferfish (3.0.0)\n"
	dir, pkgs := parseList(in)
	if dir != "/Users/x/Library/Application Support/McNeel/Rhinoceros/packages/8.0" {
		t.Fatalf("dir = %q", dir)
	}
	want := []Package{{"rhino-mcp-bridge", "0.0.1"}, {"Pufferfish", "3.0.0"}}
	if !reflect.DeepEqual(pkgs, want) {
		t.Fatalf("pkgs = %#v, want %#v", pkgs, want)
	}
}

// fakeRun records the args a command was invoked with and returns canned output.
func fakeRun(stdout, stderr string, err error, captured *[]string) func(context.Context, string, ...string) (string, string, error) {
	return func(_ context.Context, _ string, args ...string) (string, string, error) {
		*captured = args
		return stdout, stderr, err
	}
}

func TestSearch_BuildsArgs(t *testing.T) {
	var args []string
	c := &Client{exe: "yak", run: fakeRun("LunchBox (1.0.0)\n", "", nil, &args)}
	pkgs, err := c.Search(context.Background(), "box", true)
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(args, []string{"search", "--prerelease", "box"}) {
		t.Fatalf("args = %#v", args)
	}
	if len(pkgs) != 1 || pkgs[0].Name != "LunchBox" {
		t.Fatalf("pkgs = %#v", pkgs)
	}
}

func TestInstall_BuildsArgsWithVersion(t *testing.T) {
	var args []string
	c := &Client{exe: "yak", run: fakeRun("Downloading...\nInstalled.", "", nil, &args)}
	out, err := c.Install(context.Background(), "Pufferfish", "3.0.0")
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(args, []string{"install", "Pufferfish", "3.0.0"}) {
		t.Fatalf("args = %#v", args)
	}
	if out == "" {
		t.Fatal("expected combined output")
	}
}

func TestInstall_OmitsEmptyVersion(t *testing.T) {
	var args []string
	c := &Client{exe: "yak", run: fakeRun("Installed.", "", nil, &args)}
	if _, err := c.Install(context.Background(), "Pufferfish", ""); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(args, []string{"install", "Pufferfish"}) {
		t.Fatalf("args = %#v", args)
	}
}

func TestInstall_SurfacesStderrOnError(t *testing.T) {
	var args []string
	c := &Client{exe: "yak", run: fakeRun("", "package 'nope' not found", errors.New("exit status 1"), &args)}
	_, err := c.Install(context.Background(), "nope", "")
	if err == nil {
		t.Fatal("expected an error")
	}
	if want := "package 'nope' not found"; !contains(err.Error(), want) {
		t.Fatalf("error %q should surface %q", err.Error(), want)
	}
}

func TestUninstall_BuildsArgs(t *testing.T) {
	var args []string
	c := &Client{exe: "yak", run: fakeRun("Uninstalled.", "", nil, &args)}
	if _, err := c.Uninstall(context.Background(), "Pufferfish"); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(args, []string{"uninstall", "Pufferfish"}) {
		t.Fatalf("args = %#v", args)
	}
}

func contains(s, sub string) bool {
	return len(s) >= len(sub) && (s == sub || indexOf(s, sub) >= 0)
}

func indexOf(s, sub string) int {
	for i := 0; i+len(sub) <= len(s); i++ {
		if s[i:i+len(sub)] == sub {
			return i
		}
	}
	return -1
}
