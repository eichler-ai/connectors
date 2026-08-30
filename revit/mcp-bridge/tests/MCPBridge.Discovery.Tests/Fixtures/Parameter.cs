namespace MCPBridge.Discovery.Tests.Fixtures;

/// <summary>
/// Pairs with <see cref="ParameterSet"/> to reproduce the constructor-inflation case from the real corpus:
/// a type whose NAME is exactly the two query words, so its constructor (whose reflected member name is the
/// type name) would score those words twice if constructors were scored like ordinary members.
/// </summary>
public class Parameter
{
    /// <summary>Sets this parameter to a value.</summary>
    public void Set(double value)
    {
    }
}

/// <summary>A set of parameters. Exists for its CONSTRUCTOR, which is the member under test.</summary>
public class ParameterSet
{
    /// <summary>Creates an empty ParameterSet.</summary>
    public ParameterSet()
    {
    }
}
