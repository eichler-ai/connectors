using System.Collections.Generic;

namespace Rhino.MCPBridge.Core.Protocol;

/// <summary>
/// Everything `register` says about this Rhino process (PRD §05). Built on the main thread by the
/// adapter, sent on every connection's auth and re-sent on every document event; immutable so a
/// snapshot taken on the main thread can be handed to the connection threads without a lock.
/// </summary>
public sealed class RegisterSnapshot
{
    public Guid InstanceId { get; }
    public int Pid { get; }
    public string RhinoVersion { get; }
    /// <summary>"macos" | "windows" — the value list_instances reports.</summary>
    public string Platform { get; }
    public string BridgeVersion { get; }
    public IReadOnlyList<RegisteredDocument> Documents { get; }
    /// <summary>"idle" | "busy" | "unrecoverable" at the time the snapshot was built (PRD §05).</summary>
    public string ExecutionState { get; }

    public RegisterSnapshot(Guid instanceId, int pid, string rhinoVersion, string platform, string bridgeVersion, IReadOnlyList<RegisteredDocument> documents, string executionState = "idle")
    {
        ExecutionState = executionState;
        InstanceId = instanceId;
        Pid = pid;
        RhinoVersion = rhinoVersion;
        Platform = platform;
        BridgeVersion = bridgeVersion;
        Documents = documents;
    }
}
