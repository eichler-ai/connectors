namespace MCPBridge.Core.Connection;

/// <summary>Local vs. remote topology, PRD §05.</summary>
public enum BrokerTopologyMode
{
    /// <summary>Broker and Revit on the same OS instance -- the real target deployment.</summary>
    Local,

    /// <summary>Broker and Revit on different machines -- e.g. broker on the Mac, Revit in the Parallels VM.</summary>
    Remote,
}
