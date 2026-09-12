package mcpserver

import (
	"context"
	"errors"
	"testing"

	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/yak"
)

// fakePM records calls and returns canned results.
type fakePM struct {
	installed    [][2]string // (name, version) pairs actually installed
	uninstalled  []string
	searchResult []yak.Package
	installErr   error
}

func (f *fakePM) Search(_ context.Context, q string, _ bool) ([]yak.Package, error) {
	return f.searchResult, nil
}
func (f *fakePM) List(_ context.Context) (string, []yak.Package, error) {
	return "/pkgs", f.searchResult, nil
}
func (f *fakePM) Install(_ context.Context, name, version string) (string, error) {
	if f.installErr != nil {
		return "", f.installErr
	}
	f.installed = append(f.installed, [2]string{name, version})
	return "Installed.", nil
}
func (f *fakePM) Uninstall(_ context.Context, name string) (string, error) {
	f.uninstalled = append(f.uninstalled, name)
	return "Uninstalled.", nil
}

func TestInstall_PreviewWithoutConfirm_DoesNothing(t *testing.T) {
	f := &fakePM{}
	out := doInstall(context.Background(), f, InstallPackageIn{Name: "Pufferfish"})
	if out.Status != "preview" {
		t.Fatalf("status = %q, want preview", out.Status)
	}
	if len(f.installed) != 0 {
		t.Fatalf("preview must not install; got %v", f.installed)
	}
	if out.Notice == "" {
		t.Fatal("preview should carry a notice telling the caller to confirm")
	}
}

func TestInstall_WithConfirm_Installs(t *testing.T) {
	f := &fakePM{}
	out := doInstall(context.Background(), f, InstallPackageIn{Name: "Pufferfish", Version: "3.0.0", ConfirmLifecycleActions: true})
	if out.Status != "installed" {
		t.Fatalf("status = %q, want installed", out.Status)
	}
	if len(f.installed) != 1 || f.installed[0] != [2]string{"Pufferfish", "3.0.0"} {
		t.Fatalf("installed = %v", f.installed)
	}
	if out.Notice == "" || !contains(out.Notice, "Restart") {
		t.Fatalf("installed result should tell the user to restart Rhino, got %q", out.Notice)
	}
}

func TestInstall_MissingName_Errors(t *testing.T) {
	f := &fakePM{}
	out := doInstall(context.Background(), f, InstallPackageIn{ConfirmLifecycleActions: true})
	if out.Status != "error" || out.Error == nil {
		t.Fatalf("expected an error for a missing name, got %+v", out)
	}
	if len(f.installed) != 0 {
		t.Fatal("must not install with no name")
	}
}

func TestInstall_SurfacesYakError(t *testing.T) {
	f := &fakePM{installErr: errors.New("yak install failed: not found")}
	out := doInstall(context.Background(), f, InstallPackageIn{Name: "nope", ConfirmLifecycleActions: true})
	if out.Status != "error" || out.Error == nil {
		t.Fatalf("expected an error, got %+v", out)
	}
}

func TestUninstall_PreviewWithoutConfirm_DoesNothing(t *testing.T) {
	f := &fakePM{}
	out := doUninstall(context.Background(), f, UninstallPackageIn{Name: "Pufferfish"})
	if out.Status != "preview" {
		t.Fatalf("status = %q, want preview", out.Status)
	}
	if len(f.uninstalled) != 0 {
		t.Fatalf("preview must not uninstall; got %v", f.uninstalled)
	}
}

func TestUninstall_WithConfirm_Uninstalls(t *testing.T) {
	f := &fakePM{}
	out := doUninstall(context.Background(), f, UninstallPackageIn{Name: "Pufferfish", ConfirmLifecycleActions: true})
	if out.Status != "uninstalled" {
		t.Fatalf("status = %q, want uninstalled", out.Status)
	}
	if len(f.uninstalled) != 1 || f.uninstalled[0] != "Pufferfish" {
		t.Fatalf("uninstalled = %v", f.uninstalled)
	}
}

func TestSearch_EmptyGivesGuidance(t *testing.T) {
	f := &fakePM{searchResult: nil}
	out := doSearch(context.Background(), f, SearchPackagesIn{Query: "zzz"})
	if len(out.Packages) != 0 || out.Guidance == "" {
		t.Fatalf("empty search should give guidance, got %+v", out)
	}
}

func TestSearch_MapsPackages(t *testing.T) {
	f := &fakePM{searchResult: []yak.Package{{Name: "LunchBox", Version: "1.0"}}}
	out := doSearch(context.Background(), f, SearchPackagesIn{Query: "box"})
	if len(out.Packages) != 1 || out.Packages[0].Name != "LunchBox" {
		t.Fatalf("packages = %+v", out.Packages)
	}
}

func contains(s, sub string) bool {
	for i := 0; i+len(sub) <= len(s); i++ {
		if s[i:i+len(sub)] == sub {
			return true
		}
	}
	return false
}
