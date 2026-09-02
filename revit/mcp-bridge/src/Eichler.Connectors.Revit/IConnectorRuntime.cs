using System.Collections.Generic;

namespace Eichler.Connectors.Revit;

/// <summary>
/// The per-run implementation behind <see cref="Connector"/>, supplied by MCPBridge.Core.
///
/// <para>INTERNAL, and that is the point of this file: <see cref="Connector"/> is the only type in this
/// assembly an agent can discover, because DiscoveryReflector indexes publicly visible types only.
/// Everything a script is meant to see is on <see cref="Connector"/>; everything that makes it work is
/// behind this seam.</para>
///
/// <para>NO REVIT TYPES APPEAR IN THIS INTERFACE, and that is a hard constraint rather than a
/// preference. MCPBridge.Core's ScriptGlobals implements it, and the CLR builds a type's
/// interface-method table when it LOADS the type -- which means resolving every signature in every
/// interface it implements. RevitAPI.dll is mixed-mode C++/CLI and cannot load outside a live Revit
/// process, so an <c>Autodesk.Revit.DB.Document</c> anywhere in this interface makes ScriptGlobals
/// unloadable in the tier-1 test host. Found by trying it: 114 of 423 tier-1 tests failed with
/// "Could not load file or assembly 'RevitAPI'". A class's OWN members are resolved lazily, per member,
/// at JIT -- which is why ScriptGlobals can carry Document-typed properties and Connector can carry
/// Document-typed methods, while this interface cannot. The document members are therefore typed
/// <c>object</c> here and cast back in <see cref="Connector"/>, whose casts JIT only when a script
/// actually calls them, i.e. only inside Revit.</para>
///
/// <para>An INSTANCE interface, deliberately, and the alternative was considered and rejected. Every
/// member here needs state scoped to one execute_script run -- the workspace directories, that run's
/// managed transaction set, that run's published-file list. Static entry points would need ambient
/// context to reach it, which is precisely the shape round 3 of the denylist review found
/// live-exploitable (ActiveDialogContext, a public static whose mutators a script called mid-run to
/// disable dialog suppression). Keeping the seam an instance means the per-run state travels by
/// reference and never becomes reachable from a static.</para>
/// </summary>
internal interface IConnectorRuntime
{
    /// <summary>Backs <see cref="Connector.ImportsDirectory"/>.</summary>
    string? ImportsDirectory { get; }

    /// <summary>Backs <see cref="Connector.ExportsDirectory"/>.</summary>
    string? ExportsDirectory { get; }

    /// <summary>Backs <see cref="Connector.DialogResultOverrides"/>.</summary>
    IDictionary<string, int> DialogResultOverrides { get; }

    /// <summary>Backs <see cref="Connector.Publish"/>. Never throws -- see that method.</summary>
    void Publish(string sourcePath, string? name);

    /// <summary>Backs <see cref="Connector.CreateProjectDocument"/>. Returns an
    /// <c>Autodesk.Revit.DB.Document</c> as <c>object</c> -- see the type-load note above.</summary>
    object CreateProjectDocument(string? templatePath);

    /// <summary>Backs <see cref="Connector.CreateFamilyDocument"/>. Returns an
    /// <c>Autodesk.Revit.DB.Document</c> as <c>object</c> -- see the type-load note above.</summary>
    object CreateFamilyDocument(string templatePath);

    /// <summary>Backs <see cref="Connector.OpenForWriting"/>. Takes and returns an
    /// <c>Autodesk.Revit.DB.Document</c> as <c>object</c> -- see the type-load note above.</summary>
    object OpenForWriting(object document);

    /// <summary>Backs <see cref="Connector.WithoutTransaction"/>. The document is <c>object</c> for the
    /// type-load reason above; <c>System.Action</c> names no Revit type, so the callback passes through
    /// unchanged.</summary>
    void WithoutTransaction(object document, System.Action body);

    /// <summary>Backs <see cref="Connector.WithTransaction(Autodesk.Revit.DB.Document, System.Action)"/>.</summary>
    void WithTransaction(object document, System.Action body);

    /// <summary>Backs <see cref="Connector.WithTransaction{T}"/>. <c>System.Func&lt;T&gt;</c> names no
    /// Revit type, so -- like <c>System.Action</c> above -- it passes through this seam unchanged; only
    /// the document is erased to <c>object</c>.</summary>
    T WithTransaction<T>(object document, System.Func<T> body);

    /// <summary>Backs <see cref="Connector.Settle"/>.</summary>
    void Settle(object document, bool keep);
}
