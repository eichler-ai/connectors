namespace Rhino.MCPBridge.Discovery.Tests.Fixtures;

/// <summary>
/// A reflection fixture whose members exercise the CPython call-shape rendering (PRD §09 "both call
/// shapes", <see cref="Rhino.MCPBridge.Core.Discovery.SignatureFormatter.BuildPythonCall"/>): the ways .NET
/// interop from the Rhino CPython host diverges from C# — <c>out</c>/<c>ref</c> parameters returned as a
/// tuple, an <c>in</c> (readonly-ref) parameter passed like a value, a generic method's explicit type
/// arguments, static-vs-instance qualification, and a constructor called without <c>new</c>.
/// </summary>
public class Interop
{
    /// <summary>Creates an interop helper with an initial seed.</summary>
    /// <param name="seed">The starting value.</param>
    public Interop(int seed)
    {
        Seed = seed;
    }

    /// <summary>The seed this helper was created with.</summary>
    public int Seed { get; }

    /// <summary>The shared tolerance used across interop helpers.</summary>
    public static double Tolerance => 1e-9;

    /// <summary>Parses a "x,y" pair, returning whether it succeeded and the two coordinates.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="x">The parsed X coordinate.</param>
    /// <param name="y">The parsed Y coordinate.</param>
    /// <returns>True when the text parsed.</returns>
    public static bool TryParsePoint(string text, out double x, out double y)
    {
        x = 0;
        y = 0;
        return !string.IsNullOrEmpty(text);
    }

    /// <summary>Adds a value into a running total in place.</summary>
    /// <param name="total">The running total, updated in place.</param>
    /// <param name="add">The value to add.</param>
    public void Accumulate(ref int total, int add)
    {
        total += add;
    }

    /// <summary>Records a large reading without copying it.</summary>
    /// <param name="reading">The reading to record, passed by readonly reference.</param>
    public void Record(in double reading)
    {
        _ = reading;
    }

    /// <summary>Returns its argument unchanged, whatever its type.</summary>
    /// <param name="value">The value to echo.</param>
    /// <returns>The same value.</returns>
    public T Echo<T>(T value) => value;

    /// <summary>Measures the distance to another value.</summary>
    /// <param name="other">The other value.</param>
    /// <returns>The absolute difference.</returns>
    public double DistanceTo(double other) => other;
}
