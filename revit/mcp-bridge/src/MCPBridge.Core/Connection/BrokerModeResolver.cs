using System;
using System.Collections.Generic;
using MCPBridge.Core.Diagnostics;

namespace MCPBridge.Core.Connection;

/// <summary>
/// Decides which broker topology the add-in dials (issue #185). Precedence, highest first:
/// <list type="number">
/// <item><b><see cref="BridgeConfig"/></b> (<c>bridge-config.json</c>) -- written by the ribbon's
/// broker-mode switch, so a deliberate in-app choice is authoritative and survives restarts.</item>
/// <item><b>Environment</b> -- <c>MCPBRIDGE_BROKER_MODE=remote</c> + <c>MCPBRIDGE_SHARED_ROOT</c>, the
/// original and only pre-#185 mechanism, kept so the existing dev launchers
/// (<c>dev-tooling/launch-revit-discovery.bat</c>, <c>install-mac.sh</c>, <c>docs/quickstart.md</c>)
/// keep working unchanged.</item>
/// <item><b>Default</b> -- Local (PRD §05: "the real target deployment").</item>
/// </list>
///
/// <para>Config-over-env is the order that fixes the symptom #185 was filed over: a leftover
/// <c>MCPBRIDGE_BROKER_MODE=remote</c> in a user's environment silently forced a fresh, correct local
/// install into remote mode, and the only cure was clearing it at User and Machine scope AND
/// relaunching Revit from a shell that had never seen it. With this order, one click on the ribbon
/// switch writes <c>{"brokerMode":"local"}</c> and the environment stops mattering. Note the config
/// therefore wins even when it says "local" -- a config that only counted when it said "remote" would
/// leave the leftover-env trap exactly as it was.</para>
///
/// <para>Remote mode's shared root comes from the config when it has one, else the environment --
/// so a config that says "remote" without a root still works on a machine whose launcher supplies
/// <c>MCPBRIDGE_SHARED_ROOT</c>. A remote decision with NO usable root anywhere falls back to Local
/// with a diagnostic (never a throw: <c>BuildDiscoveryOptions</c> has always refused to fail the
/// whole add-in load over a topology setting, and that rule stands).</para>
///
/// <para>Pure function of its inputs, no I/O -- the file read is <see cref="BridgeConfig.Load"/>'s
/// job and the environment is injected -- which is what makes the precedence table above directly
/// unit-testable in MCPBridge.Core.Tests.</para>
/// </summary>
internal static class BrokerModeResolver
{
    public const string ModeVariable = "MCPBRIDGE_BROKER_MODE";
    public const string SharedRootVariable = "MCPBRIDGE_SHARED_ROOT";

    /// <summary>Where the winning decision came from -- logged at startup (startup-errors.log's
    /// "broker mode decided by ..." line), so "why is Revit dialing THAT broker" is answerable without
    /// a debugger.</summary>
    public enum DecisionSource
    {
        Default,
        Environment,
        Config,
    }

    /// <summary><paramref name="Diagnostic"/> explains a fallback in the WINNING source (remote chosen but
    /// unusable, or an unknown config mode with nothing else to say); <paramref name="ConfigDiagnostic"/>
    /// is set additionally when the config was rejected AND the environment then also had something to
    /// report, so neither fault hides the other (second independent review, #187).</summary>
    public sealed record Resolution(BrokerDiscoveryOptions Options, DecisionSource Source, DiagnosticRecord? Diagnostic, DiagnosticRecord? ConfigDiagnostic = null);

    public static Resolution Resolve(BridgeConfig? config, Func<string, string?> getEnvironmentVariable)
    {
        var envMode = getEnvironmentVariable(ModeVariable);
        var envRoot = getEnvironmentVariable(SharedRootVariable);

        // 1. Config, when it states a mode at all. An absent/blank brokerMode means "nothing decided
        //    here" and drops through to the environment -- the file may exist purely to remember a
        //    sharedRoot for the next switch. Only the two known values count as a decision
        //    (independent PR review finding, #187): a typo in a hand-edited file used to be read as
        //    "local, decided by config", silently -- §01 says it must leave a trace, and it must not
        //    outrank an environment that IS well-formed.
        DiagnosticRecord? invalidModeDiagnostic = null;
        if (config is not null && !string.IsNullOrWhiteSpace(config.BrokerMode))
        {
            if (config.IsRemote)
            {
                var root = FirstNonBlank(config.SharedRoot, envRoot);
                return TryRemote(root, DecisionSource.Config, "bridge-config.json says brokerMode=remote");
            }

            if (config.IsLocal)
            {
                return new Resolution(BrokerDiscoveryOptions.Local(), DecisionSource.Config, null);
            }

            invalidModeDiagnostic = DiagnosticRecord.Create(
                DiagnosticSeverity.Warning,
                "bridge-config-invalid-mode",
                DiagnosticSource.Connection,
                $"bridge-config.json has brokerMode='{config.BrokerMode}', which is neither 'local' nor 'remote'; ignoring it and resolving broker mode from the environment/default instead.",
                detail: new Dictionary<string, object?> { ["broker_mode"] = config.BrokerMode },
                remedy: new[] { "Set brokerMode to \"local\" or \"remote\" -- the ribbon's broker-mode switch rewrites the file correctly." });
        }

        // 2. Environment (the pre-#185 behaviour, unchanged).
        if (string.Equals(envMode?.Trim(), BridgeConfig.RemoteMode, StringComparison.OrdinalIgnoreCase))
        {
            var remote = TryRemote(envRoot, DecisionSource.Environment, $"{ModeVariable}=remote is set");
            return remote.Diagnostic is null
                ? remote with { Diagnostic = invalidModeDiagnostic }
                : remote with { ConfigDiagnostic = invalidModeDiagnostic };
        }

        // 3. Default.
        return new Resolution(BrokerDiscoveryOptions.Local(), DecisionSource.Default, invalidModeDiagnostic);
    }

    private static Resolution TryRemote(string? sharedRoot, DecisionSource source, string decidedBy)
    {
        if (string.IsNullOrWhiteSpace(sharedRoot))
        {
            return LocalFallback(source, decidedBy, "no shared root is configured (neither bridge-config.json's sharedRoot nor MCPBRIDGE_SHARED_ROOT)");
        }

        try
        {
            return new Resolution(BrokerDiscoveryOptions.Remote(sharedRoot.Trim()), source, null);
        }
        catch (ArgumentException ex)
        {
            return LocalFallback(source, decidedBy, $"shared root '{sharedRoot}' was rejected: {ex.Message}");
        }
    }

    private static Resolution LocalFallback(DecisionSource source, string decidedBy, string why)
    {
        var diagnostic = DiagnosticRecord.Create(
            DiagnosticSeverity.Warning,
            "broker-mode-remote-unusable",
            DiagnosticSource.Connection,
            $"{decidedBy}, but {why}; falling back to LOCAL mode for this session.",
            detail: new Dictionary<string, object?> { ["decided_by"] = source.ToString() },
            remedy: new[]
            {
                @"Give remote mode a UNC shared root (\\host\share) -- via the ribbon's broker-mode switch, bridge-config.json's sharedRoot, or MCPBRIDGE_SHARED_ROOT -- or switch to local mode explicitly.",
            });
        return new Resolution(BrokerDiscoveryOptions.Local(), source, diagnostic);
    }

    private static string? FirstNonBlank(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first) ? first : (!string.IsNullOrWhiteSpace(second) ? second : null);
}
