using Rhino.MCPBridge.Core.Discovery;
using Rhino.MCPBridge.Discovery.Tests.Fixtures;
using Xunit;

namespace Rhino.MCPBridge.Discovery.Tests;

/// <summary>
/// describe_function's "both call shapes" (PRD §09): the C# <c>signature</c> and the CPython
/// <c>python_call</c> form. Driven end-to-end through <see cref="DiscoveryService.DescribeFunction"/> over
/// the Interop fixture, so the whole path is exercised — reflection, the python_call column round-trip, and
/// the resolved-single result — not just <see cref="SignatureFormatter"/> in isolation (which is internal).
/// The Python form must capture the interop divergences the PRD names: <c>out</c>/<c>ref</c> as a return
/// tuple, an <c>in</c> parameter passed like a value, explicit generic arguments, no <c>new</c> for a
/// constructor, and static-vs-instance qualification.
/// </summary>
public class DescribeCallShapesTests
{
    private const string T = "Rhino.MCPBridge.Discovery.Tests.Fixtures.Interop";

    private static DiscoveryService NewService()
    {
        var cache = new DiscoveryCache(":memory:");
        cache.Sync(new[] { ("core", typeof(Interop).Assembly) });
        return new DiscoveryService(cache);
    }

    private static DescribeFunctionSingle Describe(string member)
    {
        var result = NewService().DescribeFunction(member, memberId: null);
        Assert.NotNull(result.Single);
        return result.Single!;
    }

    [Fact]
    public void Constructor_DropsNew_AndQualifiesByType()
    {
        var s = Describe(T + ".ctor");
        Assert.Equal("Interop(int seed)", s.Signature);
        Assert.Equal("Interop(seed)", s.PythonCall);
    }

    [Fact]
    public void StaticMethodWithOutParams_ReturnsThemAsATuple_DroppedFromArgs()
    {
        var s = Describe(T + ".TryParsePoint");
        // C# signature keeps the out keyword so it compiles.
        Assert.Equal("bool TryParsePoint(string text, out double x, out double y)", s.Signature);
        // Python: out params leave the arg list and join the return tuple; static call is type-qualified.
        Assert.Equal("result, x, y = Interop.TryParsePoint(text)", s.PythonCall);
    }

    [Fact]
    public void RefParam_IsPassedIn_AndAlsoReturned()
    {
        var s = Describe(T + ".Accumulate");
        Assert.Equal("void Accumulate(ref int total, int add)", s.Signature);
        // total is passed AND comes back; add is passed only; void return contributes no "result".
        Assert.Equal("total = Accumulate(total, add)", s.PythonCall);
    }

    [Fact]
    public void InParam_IsPassedLikeAValue_AndDoesNotComeBack()
    {
        var s = Describe(T + ".Record");
        Assert.Equal("void Record(in double reading)", s.Signature);
        Assert.Equal("Record(reading)", s.PythonCall);
    }

    [Fact]
    public void GenericMethod_ShowsExplicitTypeArguments_InThePythonForm()
    {
        var s = Describe(T + ".Echo");
        Assert.Equal("Echo[T](value)", s.PythonCall);
    }

    [Fact]
    public void InstanceMethod_IsReceiverless_LikeTheCSharpSignature()
    {
        var s = Describe(T + ".DistanceTo");
        Assert.Equal("double DistanceTo(double other)", s.Signature);
        Assert.Equal("DistanceTo(other)", s.PythonCall);
    }

    [Fact]
    public void StaticProperty_IsTypeQualifiedAttributeAccess()
    {
        var s = Describe(T + ".Tolerance");
        Assert.Equal("Interop.Tolerance", s.PythonCall);
    }

    [Fact]
    public void InstanceProperty_IsBareAttributeAccess()
    {
        var s = Describe(T + ".Seed");
        Assert.Equal("Seed", s.PythonCall);
    }

    [Fact]
    public void BinaryOperator_RendersAsThePythonOperator_NotAnOpMethodCall()
    {
        var s = Describe(T + ".op_Addition");
        // pythonnet does not expose op_Addition as a callable; the usable form is the operator itself.
        Assert.Equal("left + right", s.PythonCall);
    }

    [Fact]
    public void UnaryOperator_RendersAsThePythonOperator()
    {
        var s = Describe(T + ".op_UnaryNegation");
        Assert.Equal("-value", s.PythonCall);
    }

    [Fact]
    public void StaticEvent_IsTypeQualified_WithTheSubscriptionShape()
    {
        var s = Describe(T + ".ToleranceChanged");
        Assert.Equal("Interop.ToleranceChanged += handler", s.PythonCall);
    }
}
