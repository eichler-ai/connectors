package mcpserver

import (
	"context"
	"encoding/json"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"github.com/eichler-ai/connectors/internal/servercore/diag"
	"github.com/eichler-ai/connectors/rhino/mcp-server/internal/yak"
)

const packagesSource = "mcp-server.internal.mcpserver"

// SearchPluginsIn is search_plugins' input (PRD §10: plug-in install/management via Yak).
type SearchPluginsIn struct {
	Query      string `json:"query" jsonschema:"text to search the Yak package server for (a plug-in name or keyword, e.g. \"lunchbox\"). Matches are fuzzy"`
	Prerelease bool   `json:"prerelease,omitempty" jsonschema:"include prerelease versions in the results; default false"`
}

// PluginOut is one package (name + version).
type PluginOut struct {
	Name    string `json:"name"`
	Version string `json:"version"`
}

// SearchPluginsOut lists the matches.
type SearchPluginsOut struct {
	Packages []PluginOut  `json:"packages"`
	Guidance string       `json:"guidance,omitempty"`
	Error    *diag.Record `json:"error,omitempty"`
}

// ListPluginsIn takes no arguments.
type ListPluginsIn struct{}

// ListPluginsOut lists the installed packages and where they live.
type ListPluginsOut struct {
	PackageDirectory string       `json:"package_directory,omitempty"`
	Packages         []PluginOut  `json:"packages"`
	Error            *diag.Record `json:"error,omitempty"`
}

// InstallPluginIn is install_plugin's input.
type InstallPluginIn struct {
	Name                    string `json:"name" jsonschema:"exact plug-in (package) name to install, from search_plugins"`
	Version                 string `json:"version,omitempty" jsonschema:"specific version to install; omit for the latest. Yak has no update command -- to update, install a newer version"`
	ConfirmLifecycleActions bool   `json:"confirm_lifecycle_actions,omitempty" jsonschema:"set true to actually install; without it the tool returns a preview and does nothing. Installing runs third-party code in the user's Rhino on its next start, so it is gated"`
}

// UninstallPluginIn is uninstall_plugin's input.
type UninstallPluginIn struct {
	Name                    string `json:"name" jsonschema:"exact installed plug-in (package) name to remove, from list_plugins"`
	ConfirmLifecycleActions bool   `json:"confirm_lifecycle_actions,omitempty" jsonschema:"set true to actually uninstall; without it the tool returns a preview and does nothing"`
}

// PluginActionOut is the shared result of install_plugin / uninstall_plugin.
type PluginActionOut struct {
	// Status is "installed", "uninstalled", "preview" (nothing was done, confirm to proceed) or "error".
	Status  string       `json:"status"`
	Name    string       `json:"name"`
	Version string       `json:"version,omitempty"`
	Output  string       `json:"output,omitempty"`
	Notice  string       `json:"notice,omitempty"`
	Error   *diag.Record `json:"error,omitempty"`
}

const restartNotice = "Restart Rhino to load it: a running Rhino (and its Grasshopper) does not pick up a newly installed plug-in until it restarts."

// packageManager is what the tools need from the Yak client (satisfied by *yak.Client), named so the
// gating logic can be tested with a fake and no real yak.
type packageManager interface {
	Search(ctx context.Context, query string, prerelease bool) ([]yak.Package, error)
	List(ctx context.Context) (dir string, pkgs []yak.Package, err error)
	Install(ctx context.Context, name, version string) (output string, err error)
	Uninstall(ctx context.Context, name string) (output string, err error)
}

func doSearch(ctx context.Context, client packageManager, in SearchPluginsIn) SearchPluginsOut {
	pkgs, err := client.Search(ctx, in.Query, in.Prerelease)
	if err != nil {
		return SearchPluginsOut{Packages: []PluginOut{}, Error: yakFailed(err)}
	}
	out := SearchPluginsOut{Packages: toPluginOut(pkgs)}
	if len(out.Packages) == 0 {
		out.Guidance = "no packages matched; try a broader keyword, or set prerelease to include prerelease versions"
	}
	return out
}

func doList(ctx context.Context, client packageManager) ListPluginsOut {
	dir, pkgs, err := client.List(ctx)
	if err != nil {
		return ListPluginsOut{Packages: []PluginOut{}, Error: yakFailed(err)}
	}
	return ListPluginsOut{PackageDirectory: dir, Packages: toPluginOut(pkgs)}
}

func doInstall(ctx context.Context, client packageManager, in InstallPluginIn) PluginActionOut {
	if in.Name == "" {
		return PluginActionOut{Status: "error", Error: invalidParam("name is required", "pass the package name from search_plugins")}
	}
	if !in.ConfirmLifecycleActions {
		return PluginActionOut{
			Status: "preview", Name: in.Name, Version: in.Version,
			Notice: "Would install " + in.Name + " from the Yak package server into the user's Rhino package folder. " +
				restartNotice + " Pass confirm_lifecycle_actions: true to proceed.",
		}
	}
	output, err := client.Install(ctx, in.Name, in.Version)
	if err != nil {
		return PluginActionOut{Status: "error", Name: in.Name, Version: in.Version, Output: output, Error: yakFailed(err)}
	}
	return PluginActionOut{Status: "installed", Name: in.Name, Version: in.Version, Output: output, Notice: restartNotice}
}

func doUninstall(ctx context.Context, client packageManager, in UninstallPluginIn) PluginActionOut {
	if in.Name == "" {
		return PluginActionOut{Status: "error", Error: invalidParam("name is required", "pass the package name from list_plugins")}
	}
	if !in.ConfirmLifecycleActions {
		return PluginActionOut{
			Status: "preview", Name: in.Name,
			Notice: "Would uninstall " + in.Name + " from the user's Rhino package folder. Pass confirm_lifecycle_actions: true to proceed.",
		}
	}
	output, err := client.Uninstall(ctx, in.Name)
	if err != nil {
		return PluginActionOut{Status: "error", Name: in.Name, Output: output, Error: yakFailed(err)}
	}
	return PluginActionOut{
		Status: "uninstalled", Name: in.Name, Output: output,
		Notice: "Uninstalled from disk; a running Rhino keeps it loaded until it restarts.",
	}
}

func actionResult(out PluginActionOut) (*mcp.CallToolResult, PluginActionOut, error) {
	if out.Status == "error" {
		return packagesErr(out), out, nil
	}
	return nil, out, nil
}

// The tool handlers, extracted so the full gate (name/confirm -> client -> mutate) is testable by
// overriding packagesClient. RegisterPlugins just wires these to the MCP tool names.

func searchTool(ctx context.Context, in SearchPluginsIn) (*mcp.CallToolResult, SearchPluginsOut, error) {
	client, drec := packagesClient()
	if drec != nil {
		out := SearchPluginsOut{Packages: []PluginOut{}, Error: drec}
		return packagesErr(out), out, nil
	}
	out := doSearch(ctx, client, in)
	if out.Error != nil {
		return packagesErr(out), out, nil
	}
	return nil, out, nil
}

func listTool(ctx context.Context) (*mcp.CallToolResult, ListPluginsOut, error) {
	client, drec := packagesClient()
	if drec != nil {
		out := ListPluginsOut{Packages: []PluginOut{}, Error: drec}
		return packagesErr(out), out, nil
	}
	out := doList(ctx, client)
	if out.Error != nil {
		return packagesErr(out), out, nil
	}
	return nil, out, nil
}

func installTool(ctx context.Context, in InstallPluginIn) (*mcp.CallToolResult, PluginActionOut, error) {
	// The validation and preview paths need no yak client; only a confirmed install shells out.
	if in.Name == "" || !in.ConfirmLifecycleActions {
		return actionResult(doInstall(ctx, nil, in))
	}
	client, drec := packagesClient()
	if drec != nil {
		out := PluginActionOut{Status: "error", Name: in.Name, Error: drec}
		return packagesErr(out), out, nil
	}
	return actionResult(doInstall(ctx, client, in))
}

func uninstallTool(ctx context.Context, in UninstallPluginIn) (*mcp.CallToolResult, PluginActionOut, error) {
	if in.Name == "" || !in.ConfirmLifecycleActions {
		return actionResult(doUninstall(ctx, nil, in))
	}
	client, drec := packagesClient()
	if drec != nil {
		out := PluginActionOut{Status: "error", Name: in.Name, Error: drec}
		return packagesErr(out), out, nil
	}
	return actionResult(doUninstall(ctx, client, in))
}

// RegisterPlugins adds search_plugins, list_plugins, install_plugin and uninstall_plugin (PRD §10):
// Grasshopper/Rhino plug-in management through Rhino's bundled Yak CLI. These act on the per-user package
// folder and need no running Rhino; a change takes effect on Rhino's next start.
func RegisterPlugins(s *mcp.Server) {
	mcp.AddTool(s, &mcp.Tool{
		Name: "search_plugins",
		Description: "Search Rhino's Yak package server for Grasshopper/Rhino plug-ins by name or keyword. " +
			"Read-only. Returns matching packages with their latest version; use install_plugin to install one. " +
			"Matches are fuzzy, so an empty or broad query returns many results.",
	}, func(ctx context.Context, _ *mcp.CallToolRequest, in SearchPluginsIn) (*mcp.CallToolResult, SearchPluginsOut, error) {
		return searchTool(ctx, in)
	})

	mcp.AddTool(s, &mcp.Tool{
		Name: "list_plugins",
		Description: "List the Rhino/Grasshopper plug-in packages installed for this user, with the folder Rhino loads them from. " +
			"Read-only.",
	}, func(ctx context.Context, _ *mcp.CallToolRequest, _ ListPluginsIn) (*mcp.CallToolResult, ListPluginsOut, error) {
		return listTool(ctx)
	})

	mcp.AddTool(s, &mcp.Tool{
		Name: "install_plugin",
		Description: "Install a Grasshopper/Rhino plug-in from Rhino's Yak package server. Gated: without " +
			"confirm_lifecycle_actions it returns a preview and installs nothing (installing runs third-party code in " +
			"the user's Rhino on its next start). On success the plug-in is on disk but NOT yet loaded -- Rhino must " +
			"restart to load it. Yak has no update command: to update, install a newer version.",
	}, func(ctx context.Context, _ *mcp.CallToolRequest, in InstallPluginIn) (*mcp.CallToolResult, PluginActionOut, error) {
		return installTool(ctx, in)
	})

	mcp.AddTool(s, &mcp.Tool{
		Name: "uninstall_plugin",
		Description: "Uninstall a Rhino/Grasshopper plug-in package for this user. Gated: without confirm_lifecycle_actions " +
			"it returns a preview and removes nothing. The plug-in stays loaded in any running Rhino until it restarts.",
	}, func(ctx context.Context, _ *mcp.CallToolRequest, in UninstallPluginIn) (*mcp.CallToolResult, PluginActionOut, error) {
		return uninstallTool(ctx, in)
	})
}

// packagesClient locates the yak CLI and wraps it. A var so a test can substitute a recording
// packageManager and drive the handlers without a real yak.
var packagesClient = func() (packageManager, *diag.Record) {
	exe, err := yak.Locate()
	if err != nil {
		return nil, diag.New(diag.SeverityError, "yak-not-found", packagesSource, err.Error()).
			WithRemedy("install Rhino 8 in the default location, or set RHINO_YAK_PATH to the full path of the yak binary")
	}
	return yak.New(exe), nil
}

func yakFailed(err error) *diag.Record {
	return diag.New(diag.SeverityError, "yak-command-failed", packagesSource, err.Error()).
		WithRemedy("check the package name (search_plugins) and that the machine can reach the Yak package server")
}

func invalidParam(msg, remedy string) *diag.Record {
	return diag.New(diag.SeverityError, "invalid-param", packagesSource, msg).WithRemedy(remedy)
}

func toPluginOut(pkgs []yak.Package) []PluginOut {
	out := make([]PluginOut, 0, len(pkgs))
	for _, p := range pkgs {
		out = append(out, PluginOut{Name: p.Name, Version: p.Version})
	}
	return out
}

func packagesErr(out any) *mcp.CallToolResult {
	b, _ := json.MarshalIndent(out, "", "  ")
	return &mcp.CallToolResult{IsError: true, Content: []mcp.Content{&mcp.TextContent{Text: string(b)}}}
}
