namespace MCPBridge.RevitAdapter;

/// <summary>
/// Thin seam over Autodesk.Revit.DB.Document (PRD §06/§09), used by TransactionScriptExecutor to build
/// the ambient Transaction/TransactionGroup it wraps every script run in.
///
/// Phase 3 (PRD §14, "Real Revit API access from scripts"): <see cref="RawDocument"/> is the sanctioned
/// way to reach the real Autodesk.Revit.DB.Document a script now binds to as its `Document` global. It
/// replaces the unsupported reflection-into-a-private-field workaround `skill.md` used to document.
/// CreateTransaction/CreateTransactionGroup remain executor-only concerns -- they are OUR adapter methods,
/// not real Revit API. What keeps real Document exposure safe is ScriptApiDenylist (MCPBridge.Core), which
/// rejects at compile time any script that opens its own Autodesk.Revit.DB.Transaction/TransactionGroup/
/// SubTransaction against the same document the executor has already opened one on.
///
/// THIS COMMENT USED TO CLAIM THESE METHODS "were never reachable from a script anyway", AND THAT WAS
/// FALSE -- recorded here because the claim is the kind that gets believed on re-reading. Live-verified
/// against real Revit 2027: RoslynScriptRunner.LoadableReferences() references every assembly loaded in
/// the Revit AppDomain, MCPBridge.RevitAdapter included, so while RevitDocumentAdapter was public a script
/// could write `new MCPBridge.RevitAdapter.RevitDocumentAdapter(raw).CreateTransaction("mine")` on a
/// document it had just created, start it, write an element and COMMIT -- a real, unmanaged transaction
/// the denylist never saw, because the `new Transaction(...)` happens on the line below, in our code,
/// while the denylist's AST walk only ever examines the script's OWN compilation. The same shape against
/// the ambient document was stopped only by Revit's own one-transaction-per-document rule, not by
/// anything the connector did deliberately.
///
/// The gap is closed structurally rather than by another denylist entry: every implementation of this
/// interface, and every type that can hand one out, is now `internal` to its assembly, so a script cannot
/// name them at all. See RevitDocumentAdapter and MCPBridge.Core.Execution.ManagedDocumentTransactions.
///
/// AND THIS INTERFACE IS INTERNAL TOO, WHICH THE FIRST ROUND OF THAT FIX GOT WRONG. It used to say the
/// interface "stays public because it crosses the Core/RevitAdapter seam -- which is harmless, since a
/// script can name a type it can never obtain an instance of." A second review round disproved that
/// live: RevitScriptExecutionHandler was a public type whose public Execute(UIApplication) hands a real
/// RevitUiApplicationAdapter -- typed as the then-public IUiApplicationAdapter -- to a callback the
/// CALLER supplies, and a Roslyn script submission can declare its own type implementing that callback.
/// So a script captured a live adapter, cast it to IDocumentCreationSource, created a document and
/// opened a real unmanaged Transaction on it, reported status "success", and never named one internal
/// type. Confirmed live against Revit 2027 before this fix, exactly as the round-1 bypass was.
///
/// The rule, restated so it is actually true: a public type in MCPBridge.Core/MCPBridge.RevitAdapter
/// must neither BE an adapter/adapter-producing type NOR RETURN OR YIELD one -- directly, or through a
/// caller-supplied callback or delegate. Being un-constructible is not enough; being unnameable is.
/// InternalsVisibleTo (see MCPBridge.RevitAdapter.csproj) keeps the Core/AddIn/tests seam working.
/// </summary>
internal interface IDocumentAdapter
{
    /// <summary>Human-readable title, for diagnostics only -- not a stable identity.</summary>
    string Title { get; }

    ITransactionAdapter CreateTransaction(string name);

    ITransactionGroupAdapter CreateTransactionGroup(string name);

    /// <summary>Local file path if this document has been saved, else null (PRD §09 document identity).</summary>
    string? PathName { get; }

    /// <summary>Whether this document is workshared (local or cloud/ACC central) -- PRD §09.</summary>
    bool IsWorkshared { get; }

    /// <summary>
    /// The user-visible central model path when <see cref="IsWorkshared"/> is true, else null.
    /// Never the local copy's path -- PRD §09: "per-user and regenerated on every fresh local copy".
    /// </summary>
    string? CentralModelPath { get; }

    /// <summary>
    /// This document's stable `doc-&lt;hash&gt;`/`tmp-&lt;guid&gt;` identity (PRD §09). A plain string,
    /// not a Revit type -- crosses the Core/RevitAdapter seam without Core ever needing to see the
    /// underlying Autodesk.Revit.DB.Document. Independent PR review finding: this MUST be resolved
    /// once and cached per live Document, not recomputed on every access -- resolving it fresh each
    /// time would re-mint a brand-new tmp-&lt;guid&gt; on every single call for an unsaved document
    /// (see DocumentIdentity.Resolve's own doc comment), scattering that document's published files
    /// across a different workspace directory on every execute_script call. Real implementations
    /// (RevitDocumentAdapter) delegate to DocumentIdentity's shared, process-lifetime cache so
    /// every IDocumentAdapter wrapping the same live Document -- however many times it's freshly
    /// constructed -- agrees on the same id, and so this agrees with whatever `register`/list_instances
    /// already reported for the same document.
    /// </summary>
    string DocumentId { get; }

    /// <summary>
    /// True when <paramref name="other"/> wraps the exact same underlying document as this adapter --
    /// the reference-equality backstop <see cref="MCPBridge.Core.Execution.ManagedDocumentTransactions"/>'s
    /// double-open guard uses alongside <see cref="DocumentId"/> (see that guard's own doc comment for why
    /// DocumentId alone is not enough for the primary OpenForWriting case).
    ///
    /// DELIBERATELY A PLAIN BOOL, NOT THE RAW Autodesk.Revit.DB.Document ITSELF -- exactly like
    /// <see cref="DocumentId"/> already is, and for the identical reason (see that member's own doc
    /// comment: "a plain string, not a Revit type -- crosses the Core/RevitAdapter seam without Core ever
    /// needing to see the underlying Document"). A first attempt at this backstop had ManagedDocumentTransactions
    /// pattern-match the incoming adapter against IRawDocumentSource directly. That broke every tier-1 test
    /// that exercises Open() with a fake: an `is IRawDocumentSource` check forces the CLR to fully resolve
    /// that interface's own member signatures (including RawDocument's Autodesk.Revit.DB.Document return
    /// type) to build its interface map, which throws FileNotFoundException loading RevitAPI.dll --
    /// confirmed live via `dotnet test`, even though the object under test never implements that interface
    /// and the check would have returned false. Putting the comparison behind THIS interface instead keeps
    /// Core's hot Open() path free of any Revit-typed reference, so it JITs the same for a fake as for a
    /// real adapter -- only RevitDocumentAdapter's own implementation, compiled and always loaded alongside
    /// real Revit references, ever touches Document.
    /// </summary>
    bool ReferencesSameUnderlyingDocumentAs(IDocumentAdapter other);
}
