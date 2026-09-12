package restart

import (
	"context"
	"reflect"
	"runtime"
	"testing"
	"time"
)

func TestAppBundle(t *testing.T) {
	cases := map[string]string{
		"/Applications/Rhino 8.app/Contents/MacOS/Rhinoceros": "/Applications/Rhino 8.app",
		"/Users/x/My Apps/Rhino 8.app/Contents/MacOS/Rhino":   "/Users/x/My Apps/Rhino 8.app",
		"/Applications/Rhino 8.app":                           "/Applications/Rhino 8.app",
		"/usr/bin/whatever":                                   "",
	}
	for in, want := range cases {
		if got := AppBundle(in); got != want {
			t.Errorf("AppBundle(%q) = %q, want %q", in, got, want)
		}
	}
}

func TestIsRhino(t *testing.T) {
	if !IsRhino("/Applications/Rhino 8.app/Contents/MacOS/Rhinoceros") {
		t.Error("the Rhino executable should be recognised as Rhino")
	}
	if IsRhino("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome") {
		t.Error("a non-Rhino app must not be recognised as Rhino")
	}
}

func TestRestart_RefusesNonRhinoPid(t *testing.T) {
	if runtime.GOOS != "darwin" {
		t.Skip("Restart is macOS-only in v1")
	}
	// Simulates pid reuse: the pid now belongs to a different app. Restart must not kill it.
	r, _, killed := fakeRunner("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", 2)
	if _, err := Restart(context.Background(), *r, 1234, nil); err == nil {
		t.Fatal("Restart must refuse to quit a non-Rhino process")
	}
	if *killed {
		t.Fatal("Restart must NOT SIGKILL a non-Rhino process")
	}
}

func TestOpenArgs(t *testing.T) {
	got := OpenArgs("/Applications/Rhino 8.app", []string{"/a.3dm", "/b.3dm"})
	want := []string{"-a", "/Applications/Rhino 8.app", "/a.3dm", "/b.3dm"}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("OpenArgs = %#v, want %#v", got, want)
	}
	if got := OpenArgs("/Applications/Rhino 8.app", nil); !reflect.DeepEqual(got, []string{"-a", "/Applications/Rhino 8.app"}) {
		t.Fatalf("OpenArgs with no paths = %#v", got)
	}
}

// fakeRunner drives Restart without touching real processes.
func fakeRunner(execPath string, aliveFor int) (*Runner, *[]string, *bool) {
	killed := false
	alive := aliveFor
	var openedArgs []string
	r := Runner{
		ExecutablePath: func(int) (string, error) { return execPath, nil },
		Kill:           func(int) error { killed = true; return nil },
		Alive:          func(int) bool { alive--; return alive >= 0 },
		Open:           func(_ context.Context, args []string) error { openedArgs = args; return nil },
		Sleep:          func(time.Duration) {},
	}
	return &r, &openedArgs, &killed
}

func TestRestart_KillsThenRelaunchesReopeningPaths(t *testing.T) {
	if runtime.GOOS != "darwin" {
		t.Skip("Restart is macOS-only in v1")
	}
	r, opened, killed := fakeRunner("/Applications/Rhino 8.app/Contents/MacOS/Rhinoceros", 2)
	note, err := Restart(context.Background(), *r, 1234, []string{"/a.3dm"})
	if err != nil {
		t.Fatal(err)
	}
	if !*killed {
		t.Error("Restart must quit the running Rhino")
	}
	want := []string{"-a", "/Applications/Rhino 8.app", "/a.3dm"}
	if !reflect.DeepEqual(*opened, want) {
		t.Errorf("relaunch args = %#v, want %#v", *opened, want)
	}
	if note == "" {
		t.Error("Restart should return a human-readable note")
	}
}

func TestRestart_ErrorsWhenProcessNeverExits(t *testing.T) {
	if runtime.GOOS != "darwin" {
		t.Skip("Restart is macOS-only in v1")
	}
	r := Runner{
		ExecutablePath: func(int) (string, error) { return "/Applications/Rhino 8.app/Contents/MacOS/Rhinoceros", nil },
		Kill:           func(int) error { return nil },
		Alive:          func(int) bool { return true }, // never exits
		Open: func(context.Context, []string) error {
			t.Fatal("must not relaunch when the old process is still alive")
			return nil
		},
		Sleep: func(time.Duration) {},
	}
	if _, err := Restart(context.Background(), r, 1234, nil); err == nil {
		t.Fatal("expected an error when Rhino does not exit")
	}
}
