using System.Collections.Generic;

namespace Eichler.Connectors.Revit;

/// <summary>
/// Functions this connector adds on top of Revit's own API -- not part of the Revit API itself. Reach
/// them from a script through the <c>Connector</c> global, e.g. <c>Connector.Publish(path)</c>.
///
/// <para>Use these for what a script cannot do with Revit's API alone: write (inside
/// <see cref="WithTransaction(Autodesk.Revit.DB.Document, System.Action)"/>), create a document, finish a
/// document early, exchange files with the caller, and override how a dialog is answered. Revit's own
/// objects -- <c>Document</c>, <c>UIApplication</c>, <c>UIDocument</c> -- are separate globals.</para>
///
/// <para>Documents are readable but NOT modifiable until you open a <c>WithTransaction</c> block, which the
/// connector commits when it ends. Writes are kept when the script returns normally and undone if it
/// throws -- until <see cref="Settle"/> finishes one document early and permanently. A script never opens a
/// Revit <c>Transaction</c> itself; that is refused before the script runs.</para>
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
    /// <para>Never throws, so one failed file cannot abort the rest of the script: every call made while
    /// <see cref="ExportsDirectory"/> is available is reported individually as published or failed. By
    /// default a call that would overwrite an existing exported file fails rather than replacing it; pass
    /// <c>overwrite_output_files</c> on the execute_script call to allow replacement.</para>
    /// </summary>
    /// <param name="sourcePath">Path to the file to publish, typically one the script just wrote.</param>
    /// <param name="name">File name to publish under. Defaults to the source file's own name. Only the
    /// bare file name is used, so this cannot place the file outside the exports directory.</param>
    public void Publish(string sourcePath, string? name = null) => _runtime.Publish(sourcePath, name);

    /// <summary>
    /// Creates a new, blank project document, headless: in memory, with no window, no open view, and never
    /// the active document. Write to it inside <c>WithTransaction(doc, ...)</c> like any other document; a
    /// person watching Revit sees nothing appear.
    ///
    /// <para>Prefer this over <c>UIApplication.Application.NewProjectDocument</c>: the connector tracks this
    /// document for the run, so its writes are undone with everything else if the script throws.</para>
    ///
    /// <para>Being unsaved, it gets a session-only <c>tmp-</c> document id that <c>list_instances</c>
    /// reports, so a later execute_script call can target it directly. To <c>Close</c> or <c>SaveAs</c> it
    /// in THIS script, finish it first with <see cref="Settle"/>; from a later call it is already
    /// unmanaged and needs no such step. Either way you need <c>confirm_lifecycle_actions</c>. Close your
    /// scratch documents — nothing else will.</para>
    /// </summary>
    /// <remarks>
    /// The headless behaviour is deliberate, not a gap: a script-created document with a real window would
    /// steal focus from whatever a person has open, so making one visible should stay an explicit act.
    /// It takes one further call now, not two: SaveAs is reachable in the creating run once
    /// <see cref="Settle"/> has finished the document, so only OpenAndActivateDocument -- which needs a
    /// path -- has to wait for a later call. Activation itself is refused only inside a
    /// <see cref="WithTransaction(Autodesk.Revit.DB.Document, System.Action)"/> block on the ACTIVE
    /// document; between blocks it succeeds (verified live, Revit 2025 and 2027). The raw
    /// NewProjectDocument path, having no managed group, can SaveAs in its own run.
    /// </remarks>
    /// <param name="templatePath">Path to a project template. Defaults to the Revit install's own default
    /// project template, which is what a blank document needing no template asset should use.</param>
    /// <returns>The new document.</returns>
    public Autodesk.Revit.DB.Document CreateProjectDocument(string? templatePath = null) =>
        (Autodesk.Revit.DB.Document)_runtime.CreateProjectDocument(templatePath);

    /// <summary>
    /// Creates a new family document from a template. The family counterpart of
    /// <see cref="CreateProjectDocument"/>; unlike project documents there is no install-wide default
    /// family template, so a template path is required. Write to it inside <c>WithTransaction(doc, ...)</c>.
    ///
    /// <para>Headless and session-lived exactly as <see cref="CreateProjectDocument"/> describes: no
    /// window, never active, and yours to close once you are done — via <see cref="Settle"/> in this same
    /// script, or directly from a later one, with <c>confirm_lifecycle_actions</c> either way.</para>
    /// </summary>
    /// <param name="templatePath">Path to a family template (.rft).</param>
    /// <returns>The new family document.</returns>
    public Autodesk.Revit.DB.Document CreateFamilyDocument(string templatePath) =>
        (Autodesk.Revit.DB.Document)_runtime.CreateFamilyDocument(templatePath);

    /// <summary>
    /// THE way to write. Runs your code with a transaction the connector opens for this document and commits
    /// when the block ends; outside such a block every document is readable and not modifiable. One block
    /// per batch of changes, not one per element. Works on any open document — the active one, one you
    /// created, or one a previous call created — and adopts a document this run has not touched yet.
    ///
    /// <para>Closing at the end of the block is the point: calls that refuse a modifiable document —
    /// <c>Document.LoadFamily</c>, <c>UIDocument.RequestViewChange</c>, starting or committing an
    /// <c>EditScope</c> — go OUTSIDE the block. Nesting on the same document is refused; inside the block
    /// write directly. If the block throws, its changes are rolled back and the document stays usable.</para>
    /// </summary>
    /// <param name="document">The document to write to.</param>
    /// <param name="body">Your code. Runs once, immediately.</param>
    public void WithTransaction(Autodesk.Revit.DB.Document document, System.Action body) =>
        _runtime.WithTransaction(document, body);

    /// <summary>
    /// Runs your code with a transaction the connector opens for this document and commits when the block
    /// ends, and hands back what the block returned — so
    /// <c>var id = Connector.WithTransaction(Document, () => Level.Create(Document, 3.0).Id);</c> needs no
    /// local hoisted out of the block. Works on any open document, adopting one this run has not touched.
    ///
    /// <para>Nesting on the same document is refused — inside the block it is already writable, so write
    /// directly. If the block throws, its changes are rolled back and nothing is returned.</para>
    /// </summary>
    /// <typeparam name="T">Whatever your block returns.</typeparam>
    /// <param name="document">The document to write to.</param>
    /// <param name="body">Your code. Runs once, immediately; its return value is the call's result.</param>
    /// <returns>The value your block returned.</returns>
    public T WithTransaction<T>(Autodesk.Revit.DB.Document document, System.Func<T> body) =>
        _runtime.WithTransaction(document, body);

    /// <summary>
    /// Finishes this document for the rest of the run, so Revit will allow <c>Close</c>, <c>Save</c>,
    /// <c>SaveAs</c> and <c>SynchronizeWithCentral</c> on it — all of which refuse while the connector
    /// holds a group open. Call it before those, in the same script.
    ///
    /// <para><c>keep: true</c> makes everything written to this document so far PERMANENT immediately: a
    /// later failure will no longer undo it. <c>keep: false</c> discards that work, which is what you
    /// want before closing a scratch document. The choice is yours to state — the connector cannot tell
    /// what you are about to do.</para>
    ///
    /// <para>Needs <c>confirm_lifecycle_actions</c>, like the members it exists to enable. Writing to the
    /// document again afterwards is fine (<c>WithTransaction</c>, as always); nothing settled can be
    /// recovered.</para>
    /// </summary>
    /// <param name="document">The document to finish.</param>
    /// <param name="keep">True to keep this document's changes permanently; false to discard them.</param>
    public void Settle(Autodesk.Revit.DB.Document document, bool keep) =>
        _runtime.Settle(document, keep);

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
