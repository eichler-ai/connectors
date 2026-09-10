using Rhino.MCPBridge.Core.Execution;
using Xunit;

namespace Rhino.MCPBridge.Core.Tests.Execution;

public class RoslynAssemblyIsolationTests
{
    [Fact]
    public void EnsureInitialized_IsIdempotent_SafeToCallRepeatedly()
    {
        RoslynAssemblyIsolation.EnsureInitialized();
        RoslynAssemblyIsolation.EnsureInitialized();
        RoslynAssemblyIsolation.EnsureInitialized();

        Assert.True(RoslynAssemblyIsolation.IsInitialized);
    }
}
