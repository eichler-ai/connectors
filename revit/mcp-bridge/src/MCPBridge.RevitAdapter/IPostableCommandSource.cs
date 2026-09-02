namespace MCPBridge.RevitAdapter;

/// <summary>
/// The adapter half of the undo/redo tools (#146 Phase 2c): queues Revit's own Undo or Redo command.
/// A CAPABILITY interface like <see cref="IDocumentChangeSource"/>, implemented only by the real
/// <see cref="RevitUiApplicationAdapter"/>.
///
/// WHY POSTED, NOT CALLED. Revit exposes no <c>Document.Undo()</c>; the only lever is
/// <c>UIApplication.PostCommand(PostableCommand.Undo)</c>, which queues the command to run AFTER control
/// leaves the API context -- i.e. after the ExternalEvent work item that posted it has returned. The
/// effect is therefore observed, not returned: the coordinator subscribes to DocumentChanged first, posts,
/// and waits for the event the undo raises (verified live, Revit 2025: an undo raises DocumentChanged
/// with the reverted transaction's names). Because it operates on Revit's GLOBAL undo stack, it reverts
/// the most recent action in the session, which may be a person's -- the tool is gated and reports the
/// transaction names it actually reverted for exactly that reason.
/// </summary>
internal interface IPostableCommandSource
{
    /// <summary>Queues Revit's Undo command. Throws if Revit refuses to post it (e.g. a modal state).</summary>
    void PostUndo();

    /// <summary>Queues Revit's Redo command.</summary>
    void PostRedo();
}
