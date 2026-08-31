using System.Collections.Generic;

namespace Eichler.Connectors.Revit;

/// <summary>
/// Functions this connector adds on top of Revit's own API. They are not part of the Revit API and are
/// not available outside this connector. Reach them from a script through the <c>Connector</c> global,
/// e.g. <c>Connector.Publish(path)</c>.
///
/// <para>Use these for the three things a script cannot do with Revit's API alone here: create a document
/// it can immediately write to, make an already-open document writable again, and hand a file back to the
/// caller. Revit's own objects arrive as separate globals -- <c>Document</c>, <c>UIApplication</c>,
/// <c>UIDocument</c> -- and are used exactly as documented by Autodesk.</para>
///
/// <para>Every document this connector opens for writing is committed when the script returns normally
/// and rolled back if it throws. A script never opens a Revit <c>Transaction</c> itself; attempting to
/// is refused before the script runs.</para>
/// </summary>
/// <remarks>
/// MAINTAINERS: the summaries in this file are the agent-facing product, shipped verbatim by
/// describe_function, and they are deliberately not where implementation rationale goes. Issue #91's D5
/// measured the previous text at 80-230 words per member, citing internal types and PRD sections, because
/// one comment was serving both an agent and a maintainer. Rationale for HOW these work lives beside the
/// implementations in MCPBridge.Core (ScriptGlobals, ManagedDocumentTransactions); rationale for WHY the
/// facade exists lives in this file's &lt;remarks&gt; and the .csproj. Only &lt;summary&gt;, &lt;param&gt;
/// and &lt;returns&gt; are read by XmlDocIndex, so &lt;remarks&gt; is safe for notes like this one.
/// </remarks>
public sealed class Connector
{
    private readonly IConnectorRuntime _runtime;

    /// <summary>
    /// INTERNAL, though the class is public. A script binds <c>Connector</c> by name from its scope and
    /// never constructs one; the executor supplies the instance. The parameter type is internal, so a
    /// public constructor would not compile here anyway.
    /// </summary>
    internal Connector(IConnectorRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>
    /// The directory holding files a person placed for this script to read. Read them with ordinary
    /// System.IO. Null when this execution has no workspace.
    /// </summary>
    public string? ImportsDirectory => _runtime.ImportsDirectory;

    /// <summary>
    /// The directory <see cref="Publish"/> copies into, and where a previous run's published files can be
    /// read back from with ordinary System.IO. Null when this execution has no workspace.
    /// </summary>
    public string? ExportsDirectory => _runtime.ExportsDirectory;

    /// <summary>
    /// Hands a file back to the caller by copying it into <see cref="ExportsDirectory"/>, where it is
    /// reported in the execution result. The source file is copied, not moved.
    ///
    /// <para>Never throws, so one failed file cannot abort the rest of the script: every call is reported
    /// individually as published or failed. By default a call that would overwrite an existing exported
    /// file fails rather than replacing it; pass <c>overwrite_output_files</c> on the execute_script call
    /// to allow replacement.</para>
    /// </summary>
    /// <param name="sourcePath">Path to the file to publish, typically one the script just wrote.</param>
    /// <param name="name">File name to publish under. Defaults to the source file's own name. Only the
    /// bare file name is used, so this cannot place the file outside the exports directory.</param>
    public void Publish(string sourcePath, string? name = null) => _runtime.Publish(sourcePath, name);

    /// <summary>
    /// Creates a new, blank project document and opens it for writing, so the script can modify it
    /// immediately.
    ///
    /// <para>Use this rather than <c>UIApplication.Application.NewProjectDocument</c> whenever the script
    /// intends to write to the document: that returns a document nothing has opened for writing, and a
    /// script may not open one itself. The document is unsaved and in memory, and stays open for the rest
    /// of the Revit session, so a later execute_script call can find it by title.</para>
    /// </summary>
    /// <param name="templatePath">Path to a project template. Defaults to the Revit install's own default
    /// project template, which is what a blank document needing no template asset should use.</param>
    /// <returns>The new document, open for writing.</returns>
    public Autodesk.Revit.DB.Document CreateProjectDocument(string? templatePath = null) =>
        (Autodesk.Revit.DB.Document)_runtime.CreateProjectDocument(templatePath);

    /// <summary>
    /// Creates a new family document from a template and opens it for writing. The family counterpart of
    /// <see cref="CreateProjectDocument"/>; unlike project documents there is no install-wide default
    /// family template, so a template path is required.
    /// </summary>
    /// <param name="templatePath">Path to a family template (.rft).</param>
    /// <returns>The new family document, open for writing.</returns>
    public Autodesk.Revit.DB.Document CreateFamilyDocument(string templatePath) =>
        (Autodesk.Revit.DB.Document)_runtime.CreateFamilyDocument(templatePath);

    /// <summary>
    /// Opens an already-open document for writing so this script can modify it -- typically one a previous
    /// execute_script call created and left open, found by iterating
    /// <c>UIApplication.Application.Documents</c>. Without this such a document is readable but not
    /// writable, because the call that created it committed and closed its transaction when it returned.
    ///
    /// <para>Not needed for the script's own <c>Document</c>, or for a document created earlier in this
    /// same script: both are already open for writing, and calling this on them fails.</para>
    ///
    /// <para>Order matters against Revit APIs that manage their own transactions, such as
    /// <c>Document.LoadFamily</c>: make that call first and open the document for writing afterwards, or
    /// the API refuses to run.</para>
    /// </summary>
    /// <param name="document">A document found in this Revit session that is not already open for writing.</param>
    /// <returns>The same document, now open for writing. Callers already holding it can ignore this.</returns>
    public Autodesk.Revit.DB.Document OpenForWriting(Autodesk.Revit.DB.Document document) =>
        (Autodesk.Revit.DB.Document)_runtime.OpenForWriting(document);

    /// <summary>
    /// Overrides how this script answers a specific Revit dialog, for the dialogs whose default answer is
    /// not the one the script wants. Keyed by Revit's dialog id, valued with the raw result to return, and
    /// set before the action that triggers the dialog, e.g.
    /// <c>Connector.DialogResultOverrides["TaskDialog_Some_Id"] = 1001;</c>.
    ///
    /// <para>Dialogs are answered automatically whether or not this is set -- an unanswered modal dialog
    /// would stall the script -- and every automatic answer is reported in the execution result.</para>
    /// </summary>
    public IDictionary<string, int> DialogResultOverrides => _runtime.DialogResultOverrides;
}
