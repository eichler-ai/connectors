// Package appdata resolves the connector's per-machine state directory
// (CONVENTIONS.md "App-data layout"): the platform's LOCAL app-data directory
// plus Connectors/Rhino. The rule is spelled out per OS rather than taken from
// os.UserConfigDir alone, because on Windows that returns the ROAMING profile
// (%AppData%) while the plug-in writes under %LOCALAPPDATA% -- and both sides
// MUST agree on where instances/<pid>.json lives or the server never finds a
// Rhino. Mirrored in the plug-in's AppDataPaths; change both.
package appdata

import (
	"os"
	"path/filepath"
	"runtime"
)

// ConnectorRoot is <local app data>/Connectors/Rhino for the current user.
func ConnectorRoot() (string, error) {
	switch runtime.GOOS {
	case "windows":
		if local := os.Getenv("LOCALAPPDATA"); local != "" {
			return filepath.Join(local, "Connectors", "Rhino"), nil
		}
		home, err := os.UserHomeDir()
		if err != nil {
			return "", err
		}
		return filepath.Join(home, "AppData", "Local", "Connectors", "Rhino"), nil
	case "darwin":
		home, err := os.UserHomeDir()
		if err != nil {
			return "", err
		}
		return filepath.Join(home, "Library", "Application Support", "Connectors", "Rhino"), nil
	default:
		if xdg := os.Getenv("XDG_DATA_HOME"); xdg != "" {
			return filepath.Join(xdg, "Connectors", "Rhino"), nil
		}
		home, err := os.UserHomeDir()
		if err != nil {
			return "", err
		}
		return filepath.Join(home, ".local", "share", "Connectors", "Rhino"), nil
	}
}

// InstancesDir is where every running plug-in publishes its instances/<pid>.json (PRD §05).
func InstancesDir(connectorRoot string) string { return filepath.Join(connectorRoot, "instances") }
