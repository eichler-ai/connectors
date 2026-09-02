using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MCPBridge.Core.Diagnostics;
using MCPBridge.Core.Execution;
using MCPBridge.Core.Tests.Fakes;
using MCPBridge.RevitAdapter;
using Microsoft.CodeAnalysis.Scripting;
using Xunit;

namespace MCPBridge.Core.Tests.Execution;

public class TransactionScriptExecutorTests
{
    // RevitAPI/RevitAPIUI are supplied as METADATA references, not loaded assemblies -- ScriptGlobals'
    // members are real Revit types as of PRD §14, so without them nothing here would bind. See
    // RevitApiReference's doc comment for why they cannot simply be loaded.
    private static TransactionScriptExecutor NewExecutor() =>
        new(new RoslynScriptRunner(additionalMetadataReferences: RevitApiReference.References));

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcpbridge-publish-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task SuccessfulScript_CommitsTransaction_AndAssimilatesGroup()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();

        var outcome = await executor.ExecuteAsync(document, uiApp, null, "1 + 1", CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(new[] { "Start", "Commit", "Dispose" }, document.LastTransaction!.Calls);
        Assert.Equal(new[] { "Start", "Assimilate", "Dispose" }, document.LastTransactionGroup!.Calls);
    }

    // ------------------------------------------------------------------------------------------
    // #146 Phase 2: the mutation report rides the run's DocumentChanged subscription
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task SuccessfulScript_ReportsNetMutations_FromChangesRaisedDuringTheRun()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter { ActiveUiDocument = new FakeUiDocumentAdapter { Document = document } };
        // The fake raises the change INSIDE the commit, exactly where Revit raises DocumentChanged.
        document.OnTransactionCommit = () => uiApp.EmitChange(new DocumentChange(
            document.DocumentId, DocumentChangeOperation.Committed, "TransactionCommitted", new[] { "MCP Bridge Script" },
            new[] { new ChangedElement(1, "Walls"), new ChangedElement(2, "Walls") },
            new[] { new ChangedElement(9, "Levels") },
            Array.Empty<long>(),
            categoriesTruncated: false));

        var outcome = await executor.ExecuteAsync(document, uiApp, null, "1 + 1", CancellationToken.None);

        Assert.True(outcome.Success);
        var report = Assert.IsType<MutationReport>(outcome.Mutations);
        Assert.Equal(2, report.Created);
        Assert.Equal(1, report.Modified);
        Assert.Equal(2, report.ByCategory["Walls"].Created);
        // The subscription is per run: nothing may keep listening after the executor returns.
        Assert.Equal(0, uiApp.ChangeSubscribers);
    }

    [Fact]
    public async Task ReadOnlyScript_CarriesNoMutationReport()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter { ActiveUiDocument = new FakeUiDocumentAdapter { Document = document } };

        var outcome = await executor.ExecuteAsync(document, uiApp, null, "1 + 1", CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Null(outcome.Mutations);
    }

    [Fact]
    public async Task ThrowingScript_CarriesNoMutationReport_ItsChangesWereRolledBack()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter { ActiveUiDocument = new FakeUiDocumentAdapter { Document = document } };
        var subscribedDuringRun = false;

        var outcome = await executor.ExecuteAsync(document, uiApp, null,
            "throw new System.InvalidOperationException(\"boom\");", CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Null(outcome.Mutations);
        Assert.Equal(0, uiApp.ChangeSubscribers);
        _ = subscribedDuringRun;
    }

    [Fact]
    public async Task AnAdapterWithoutADocumentChangeSource_StillRuns_WithNoReport()
    {
        // Every pre-#146 fake and any future adapter that does not opt in: the report is a capability,
        // not a requirement, and its absence must not change the run.
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new NoChangeSourceUiApplicationAdapter { ActiveUiDocument = new FakeUiDocumentAdapter { Document = document } };

        var outcome = await executor.ExecuteAsync(document, uiApp, null, "1 + 1", CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Null(outcome.Mutations);
    }

    private sealed class NoChangeSourceUiApplicationAdapter : IUiApplicationAdapter
    {
        public IUiDocumentAdapter? ActiveUiDocument { get; init; }
        public System.Collections.Generic.IReadOnlyList<OpenDocumentInfo> OpenDocuments => Array.Empty<OpenDocumentInfo>();
        public IDocumentAdapter? FindOpenDocument(string documentId) => null;
    }

    [Fact]
    public async Task ThrowingScript_RollsBackTransaction_AndGroup_NeverCommits()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();

        var outcome = await executor.ExecuteAsync(
            document, uiApp, null, "throw new System.InvalidOperationException(\"boom\");", CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(new[] { "Start", "RollBack", "Dispose" }, document.LastTransaction!.Calls);
        Assert.Equal(new[] { "Start", "RollBack", "Dispose" }, document.LastTransactionGroup!.Calls);
    }

    [Fact]
    public async Task DenylistViolation_RollsBackTransactionAndGroup_NeverCommits()
    {
        // PRD §14: the ambient TransactionGroup/Transaction are opened BEFORE compilation runs, so a
        // compile-time denylist rejection has to unwind them -- through the exact same path a
        // CompilationErrorException or ScriptAwaitNotAllowedException already takes. Asserting that
        // here means the denylist needed no new failure-handling code, which is the claim the design
        // rests on.
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();

        var outcome = await executor.ExecuteAsync(
            document, uiApp, null, "new Autodesk.Revit.DB.Transaction(Document, \"x\");", CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.False(outcome.WasCancelled);
        Assert.IsType<ScriptApiDenylistViolationException>(outcome.Exception);
        Assert.Equal(new[] { "Start", "RollBack", "Dispose" }, document.LastTransaction!.Calls);
        Assert.Equal(new[] { "Start", "RollBack", "Dispose" }, document.LastTransactionGroup!.Calls);
    }

    [Fact]
    public async Task UnconfirmedLifecycleScript_RollsBackTransactionAndGroup_NeverCommits()
    {
        // PRD §14's confirmation gate refuses at RUN time rather than compile time (the flag is
        // per-request, the compilation is cached), so it is a genuinely different code path from the
        // unconditional rejection above -- and this is the assertion that it kept the property that
        // matters: refused before anything executes, both scopes rolled back, nothing committed. The
        // whole reason confirmation is the right mechanism for these members is that a rollback CANNOT
        // undo them once they run; a gate that let them run first would be pointless.
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();

        var outcome = await executor.ExecuteAsync(
            document, uiApp, null, "Document.Save();", CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.False(outcome.WasCancelled);
        var ex = Assert.IsType<ScriptApiDenylistViolationException>(outcome.Exception);
        Assert.Equal(ScriptApiDenylistViolationException.ConfirmationRequiredCode, ex.Code);
        Assert.Equal(new[] { "Start", "RollBack", "Dispose" }, document.LastTransaction!.Calls);
        Assert.Equal(new[] { "Start", "RollBack", "Dispose" }, document.LastTransactionGroup!.Calls);
    }

    [Fact]
    public async Task ConfirmedLifecycleScript_IsForwardedToTheRunner()
    {
        // The executor's own job in the gate is just to forward the flag; this pins that it actually
        // does. With confirmation the script gets past the gate and on to execution (where, in this
        // tier, it then fails on loading RevitAPI.dll -- see RoslynScriptRunnerTests) so the assertion
        // is that the refusal is no longer the confirmation one. A dropped parameter here would make
        // confirm_lifecycle_actions permanently inert with no other test noticing.
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();

        var outcome = await executor.ExecuteAsync(
            document, uiApp, null, "Document.Save();", CancellationToken.None, confirmLifecycleActions: true);

        Assert.IsNotType<ScriptApiDenylistViolationException>(outcome.Exception);
    }

    [Fact]
    public async Task CommitFailure_RollsBackTransactionAndGroup_ReportsFailure()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        // Force the commit itself to fail after a successful script run.
        var transaction = (FakeTransactionAdapter)document.CreateTransaction("pre-created");
        transaction.ThrowOnCommit = true;

        // Re-point the document's next CreateTransaction call to return our rigged fake.
        var riggedDocument = new RiggedDocumentAdapter(document, transaction);

        var outcome = await executor.ExecuteAsync(riggedDocument, uiApp, null, "1 + 1", CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(new[] { "Start", "Commit", "RollBack", "Dispose" }, transaction.Calls);
        Assert.Equal(new[] { "Start", "RollBack", "Dispose" }, riggedDocument.LastTransactionGroup!.Calls);
    }

    [Fact]
    public async Task PartialCommitNotice_DoesNotClaimSurvivingChanges_WhenNothingCommitted()
    {
        // SECOND-ROUND REVIEW FINDING. This notice is also emitted when the FIRST (here: only) document
        // fails to commit AND its own rollback throws -- an unknown-state document is the case an agent
        // most needs told about, so it cannot be the case that gets no notice. But CommittedDocuments is
        // empty there, and the message still read "...failed to commit after 0 other document(s) had
        // already committed ... so those changes remain" -- claiming surviving changes that do not
        // exist, in the one notice whose whole purpose is being honest about partial state.
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var transaction = (FakeTransactionAdapter)document.CreateTransaction("pre-created");
        transaction.ThrowOnCommit = true;
        transaction.ThrowOnRollBack = true;
        var riggedDocument = new RiggedDocumentAdapter(document, transaction);

        var outcome = await executor.ExecuteAsync(riggedDocument, uiApp, null, "1 + 1", CancellationToken.None);

        Assert.False(outcome.Success);
        var notice = Assert.Single(outcome.Notices, n => n.Code == "script-partial-commit");
        Assert.DoesNotContain("those changes remain", notice.Message);
        Assert.DoesNotContain("0 other document(s)", notice.Message);
        Assert.Contains("no changes were kept", notice.Message);
        // The unknown-state half of the report is unaffected -- it is why the notice fires at all here.
        Assert.Contains("state unknown", notice.Message);
    }

    [Fact]
    public async Task CancelledScript_RollsBackCleanly()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcome = await executor.ExecuteAsync(
            document, uiApp, null, "CancellationToken.ThrowIfCancellationRequested(); 1", cts.Token);

        Assert.False(outcome.Success);
        Assert.True(outcome.WasCancelled);
        Assert.Equal(new[] { "Start", "RollBack", "Dispose" }, document.LastTransaction!.Calls);
        Assert.Equal(new[] { "Start", "RollBack", "Dispose" }, document.LastTransactionGroup!.Calls);
    }

    [Fact]
    public async Task WarningOnlyFailure_CommitsAndReportsNotice()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var transaction = (FakeTransactionAdapter)document.CreateTransaction("pre-created");
        transaction.FailuresToReport = new[] { new FailureSummary(false, "wall is slightly off axis", "warn-def-1", System.Array.Empty<string>()) };
        var riggedDocument = new RiggedDocumentAdapter(document, transaction);

        var outcome = await executor.ExecuteAsync(riggedDocument, uiApp, null, "1 + 1", CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(new[] { "Start", "Commit", "Dispose" }, transaction.Calls);
        Assert.Equal(new[] { "Start", "Assimilate", "Dispose" }, riggedDocument.LastTransactionGroup!.Calls);
        Assert.Contains(outcome.Notices, n => n.Message.Contains("off axis"));
    }

    [Fact]
    public async Task ErrorFailure_RollsBackGroupOnly_NotTransactionAgain_ReturnsFailedWithNotices()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var transaction = (FakeTransactionAdapter)document.CreateTransaction("pre-created");
        transaction.FailuresToReport = new[] { new FailureSummary(true, "elements would be deleted", "err-def-1", System.Array.Empty<string>()) };
        var riggedDocument = new RiggedDocumentAdapter(document, transaction);

        var outcome = await executor.ExecuteAsync(riggedDocument, uiApp, null, "1 + 1", CancellationToken.None);

        Assert.False(outcome.Success);
        // Commit() already rolled the Transaction back internally (ProceedWithRollBack) -- only one
        // "Commit" call, no separate "RollBack" on the transaction itself.
        Assert.Equal(new[] { "Start", "Commit", "Dispose" }, transaction.Calls);
        Assert.Equal(new[] { "Start", "RollBack", "Dispose" }, riggedDocument.LastTransactionGroup!.Calls);
        Assert.Contains(outcome.Notices, n => n.Message.Contains("deleted"));
    }

    // --- PRD §09: Publish / files[] ---

    [Fact]
    public async Task ScriptThatPublishes_Succeeds_RecordsPublishedFile()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var tempDir = CreateTempDir();
        try
        {
            var sourcePath = Path.Combine(tempDir, "source.txt");
            File.WriteAllText(sourcePath, "hello");
            var exportsDir = Path.Combine(tempDir, "exports");
            Directory.CreateDirectory(exportsDir);

            var script = $"Connector.Publish(@\"{sourcePath}\");";
            var outcome = await executor.ExecuteAsync(document, uiApp, null, script, CancellationToken.None, exportsDir, overwriteOutputFiles: false);

            Assert.True(outcome.Success);
            var published = Assert.Single(outcome.Files);
            Assert.Equal(PublishedFileRecord.StatusPublished, published.Status);
            Assert.True(File.Exists(Path.Combine(exportsDir, "source.txt")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishCollision_OverwriteFalse_RecordsFailedNamingTheFlag_ScriptOutcomeUnaffected()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var tempDir = CreateTempDir();
        try
        {
            var sourcePath = Path.Combine(tempDir, "source.txt");
            File.WriteAllText(sourcePath, "hello");
            var exportsDir = Path.Combine(tempDir, "exports");
            Directory.CreateDirectory(exportsDir);
            var destinationPath = Path.Combine(exportsDir, "source.txt");
            File.WriteAllText(destinationPath, "existing");

            var script = $"Connector.Publish(@\"{sourcePath}\");";
            var outcome = await executor.ExecuteAsync(document, uiApp, null, script, CancellationToken.None, exportsDir, overwriteOutputFiles: false);

            Assert.True(outcome.Success); // a publish failure never rolls back or fails the script's own outcome
            var failed = Assert.Single(outcome.Files);
            Assert.Equal(PublishedFileRecord.StatusFailed, failed.Status);
            Assert.Contains("overwrite_output_files", failed.Message);
            Assert.Equal("existing", File.ReadAllText(destinationPath)); // untouched
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishCollision_OverwriteTrue_Succeeds_ReplacesDestination()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var tempDir = CreateTempDir();
        try
        {
            var sourcePath = Path.Combine(tempDir, "source.txt");
            File.WriteAllText(sourcePath, "new content");
            var exportsDir = Path.Combine(tempDir, "exports");
            Directory.CreateDirectory(exportsDir);
            var destinationPath = Path.Combine(exportsDir, "source.txt");
            File.WriteAllText(destinationPath, "existing");

            var script = $"Connector.Publish(@\"{sourcePath}\");";
            var outcome = await executor.ExecuteAsync(document, uiApp, null, script, CancellationToken.None, exportsDir, overwriteOutputFiles: true);

            Assert.True(outcome.Success);
            var published = Assert.Single(outcome.Files);
            Assert.Equal(PublishedFileRecord.StatusPublished, published.Status);
            Assert.Equal("new content", File.ReadAllText(destinationPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ScriptThatPublishesThenThrows_StillReportsPublishedFile()
    {
        // PRD §09 invariant: files[] is never conditional on the run's own outcome.
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var tempDir = CreateTempDir();
        try
        {
            var sourcePath = Path.Combine(tempDir, "source.txt");
            File.WriteAllText(sourcePath, "hello");
            var exportsDir = Path.Combine(tempDir, "exports");
            Directory.CreateDirectory(exportsDir);

            var script = $"Connector.Publish(@\"{sourcePath}\"); throw new System.InvalidOperationException(\"boom\");";
            var outcome = await executor.ExecuteAsync(document, uiApp, null, script, CancellationToken.None, exportsDir, overwriteOutputFiles: false);

            Assert.False(outcome.Success);
            var published = Assert.Single(outcome.Files);
            Assert.Equal(PublishedFileRecord.StatusPublished, published.Status);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ScriptThatPublishesThenIsCancelled_StillReportsPublishedFile()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var tempDir = CreateTempDir();
        try
        {
            var sourcePath = Path.Combine(tempDir, "source.txt");
            File.WriteAllText(sourcePath, "hello");
            var exportsDir = Path.Combine(tempDir, "exports");
            Directory.CreateDirectory(exportsDir);

            // The token is deliberately NOT pre-cancelled here: RoslynScriptRunner.RunAsync checks
            // cancellationToken.ThrowIfCancellationRequested() before the script body ever runs (see its
            // own source), so a pre-cancelled token would never let Publish() execute at all -- that would
            // test nothing about the files[]-survives-cancellation invariant this test exists to cover.
            //
            // The cancellation is GENUINE, not simulated (reworked with the v1 integrated review's OCE
            // fix): this test used to have the script throw OperationCanceledException itself with no
            // cancellation requested, leaning on exactly the misclassification RunAsync no longer has --
            // a script-thrown OCE without a signalled token is now a failure, per PRD §06's "cancelled
            // means the agent asked for this". Instead the script publishes, drops a marker file, and
            // cooperatively waits on its CancellationToken; the test cancels the token the moment the
            // marker appears -- deterministically after the publish, with no timing race.
            var publishedMarker = Path.Combine(tempDir, "published.marker");
            using var cts = new CancellationTokenSource();
            var script = $@"Connector.Publish(@""{sourcePath}"");
System.IO.File.WriteAllText(@""{publishedMarker}"", ""x"");
var sw = System.Diagnostics.Stopwatch.StartNew();
while (!CancellationToken.IsCancellationRequested && sw.ElapsedMilliseconds < 30000) System.Threading.Thread.Sleep(5);
CancellationToken.ThrowIfCancellationRequested();
throw new System.TimeoutException(""cancellation was never observed"");";
            var cancelWhenPublished = Task.Run(async () =>
            {
                // Bounded (PR review finding): if the script faults before dropping the marker, an
                // unbounded poll would turn a red test into an infinite hang. Cancel at the deadline
                // regardless -- harmless on the failure path, and the assertions below report the
                // real problem.
                var pollDeadline = System.Diagnostics.Stopwatch.StartNew();
                while (!File.Exists(publishedMarker) && pollDeadline.Elapsed < TimeSpan.FromSeconds(30))
                {
                    await Task.Delay(5);
                }

                cts.Cancel();
            });
            var outcome = await executor.ExecuteAsync(document, uiApp, null, script, cts.Token, exportsDir, overwriteOutputFiles: false);
            await cancelWhenPublished;

            Assert.True(outcome.WasCancelled);
            var published = Assert.Single(outcome.Files);
            Assert.Equal(PublishedFileRecord.StatusPublished, published.Status);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task TwoPublishCalls_FirstFailsSecondSucceeds_BothIndependentlyReported_ScriptStillSucceeds()
    {
        // PRD §09's headline batch claim: a failure on one file never rolls back or blocks another.
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var tempDir = CreateTempDir();
        try
        {
            var sourceA = Path.Combine(tempDir, "a.txt");
            File.WriteAllText(sourceA, "a");
            var sourceB = Path.Combine(tempDir, "b.txt");
            File.WriteAllText(sourceB, "b");
            var exportsDir = Path.Combine(tempDir, "exports");
            Directory.CreateDirectory(exportsDir);
            File.WriteAllText(Path.Combine(exportsDir, "a.txt"), "existing"); // collides with sourceA's publish

            var script = $"Connector.Publish(@\"{sourceA}\"); Connector.Publish(@\"{sourceB}\");";
            var outcome = await executor.ExecuteAsync(document, uiApp, null, script, CancellationToken.None, exportsDir, overwriteOutputFiles: false);

            Assert.True(outcome.Success);
            Assert.Equal(2, outcome.Files.Count);
            var failedA = outcome.Files.Single(f => f.Name == "a.txt");
            Assert.Equal(PublishedFileRecord.StatusFailed, failedA.Status);
            var publishedB = outcome.Files.Single(f => f.Name == "b.txt");
            Assert.Equal(PublishedFileRecord.StatusPublished, publishedB.Status);
            Assert.True(File.Exists(Path.Combine(exportsDir, "b.txt")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ScriptThatWritesDirectlyIntoExports_ThenPublishesSamePath_RegistersWithoutCopyingOntoItself()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var tempDir = CreateTempDir();
        try
        {
            var exportsDir = Path.Combine(tempDir, "exports");
            Directory.CreateDirectory(exportsDir);
            var alreadyThere = Path.Combine(exportsDir, "already-there.txt");

            var script = $"System.IO.File.WriteAllText(@\"{alreadyThere}\", \"hi\"); Connector.Publish(@\"{alreadyThere}\");";
            var outcome = await executor.ExecuteAsync(document, uiApp, null, script, CancellationToken.None, exportsDir, overwriteOutputFiles: false);

            Assert.True(outcome.Success);
            var published = Assert.Single(outcome.Files);
            Assert.Equal(PublishedFileRecord.StatusPublished, published.Status);
            Assert.Equal("hi", File.ReadAllText(Path.Combine(exportsDir, "already-there.txt")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishWithNameParameter_RenamesOnPublish()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var tempDir = CreateTempDir();
        try
        {
            var sourcePath = Path.Combine(tempDir, "source.txt");
            File.WriteAllText(sourcePath, "hello");
            var exportsDir = Path.Combine(tempDir, "exports");
            Directory.CreateDirectory(exportsDir);

            var script = $"Connector.Publish(@\"{sourcePath}\", \"renamed.png\");";
            var outcome = await executor.ExecuteAsync(document, uiApp, null, script, CancellationToken.None, exportsDir, overwriteOutputFiles: false);

            Assert.True(outcome.Success);
            var published = Assert.Single(outcome.Files);
            Assert.Equal(PublishedFileRecord.StatusPublished, published.Status);
            Assert.Equal("renamed.png", published.Name);
            Assert.True(File.Exists(Path.Combine(exportsDir, "renamed.png")));
            Assert.False(File.Exists(Path.Combine(exportsDir, "source.txt")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishWithTraversalName_IsConstrainedToBareFileName_NeverEscapesExports()
    {
        // Independent PR review finding: an absolute path or ..\.. in `name` must not place the
        // published file outside this document's exports directory.
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var tempDir = CreateTempDir();
        try
        {
            var sourcePath = Path.Combine(tempDir, "source.txt");
            File.WriteAllText(sourcePath, "hello");
            var exportsDir = Path.Combine(tempDir, "exports");
            Directory.CreateDirectory(exportsDir);
            var escapeAttempt = Path.Combine("..", "..", "evil.txt");

            var script = $"Connector.Publish(@\"{sourcePath}\", @\"{escapeAttempt}\");";
            var outcome = await executor.ExecuteAsync(document, uiApp, null, script, CancellationToken.None, exportsDir, overwriteOutputFiles: false);

            Assert.True(outcome.Success);
            var published = Assert.Single(outcome.Files);
            Assert.Equal(PublishedFileRecord.StatusPublished, published.Status);
            Assert.Equal("evil.txt", published.Name); // traversal stripped down to the bare file name
            Assert.True(File.Exists(Path.Combine(exportsDir, "evil.txt")));
            Assert.False(File.Exists(Path.Combine(tempDir, "evil.txt"))); // never escaped exportsDir
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishWithNonexistentSource_NeverThrows_RecordsFailed()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var tempDir = CreateTempDir();
        try
        {
            var exportsDir = Path.Combine(tempDir, "exports");
            Directory.CreateDirectory(exportsDir);
            var missingSource = Path.Combine(tempDir, "does-not-exist.txt");

            var script = $"Connector.Publish(@\"{missingSource}\");";
            var outcome = await executor.ExecuteAsync(document, uiApp, null, script, CancellationToken.None, exportsDir, overwriteOutputFiles: false);

            Assert.True(outcome.Success); // Publish never throws -- the script itself doesn't fail
            var failed = Assert.Single(outcome.Files);
            Assert.Equal(PublishedFileRecord.StatusFailed, failed.Status);
            Assert.NotNull(failed.Message);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishWithDirectoryAsSource_NeverThrows_RecordsFailed()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var tempDir = CreateTempDir();
        try
        {
            var exportsDir = Path.Combine(tempDir, "exports");
            Directory.CreateDirectory(exportsDir);
            var directoryAsSource = Path.Combine(tempDir, "a-directory");
            Directory.CreateDirectory(directoryAsSource);

            var script = $"Connector.Publish(@\"{directoryAsSource}\");";
            var outcome = await executor.ExecuteAsync(document, uiApp, null, script, CancellationToken.None, exportsDir, overwriteOutputFiles: false);

            Assert.True(outcome.Success); // Publish never throws -- the script itself doesn't fail
            var failed = Assert.Single(outcome.Files);
            Assert.Equal(PublishedFileRecord.StatusFailed, failed.Status);
            Assert.NotNull(failed.Message);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishWithSourcePathYieldingEmptyFileName_NeverFalselyPublishes()
    {
        // Second-round independent review finding: a rooted sourcePath ending in a directory
        // separator yields an empty Path.GetFileName. The old fallback ("use the raw sourcePath as
        // displayName") let Path.Combine(exportsDir, sourcePath) return the rooted sourcePath
        // verbatim -- outside exportsDir -- which then coincidentally satisfied the
        // already-in-exports containment check and recorded a false "published" for a file that was
        // never copied. Must fail outright instead.
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var tempDir = CreateTempDir();
        try
        {
            var exportsDir = Path.Combine(tempDir, "exports");
            Directory.CreateDirectory(exportsDir);
            var sourcePathWithTrailingSeparator = Path.Combine(tempDir, "a-directory") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(Path.Combine(tempDir, "a-directory"));

            var script = $"Connector.Publish(@\"{sourcePathWithTrailingSeparator}\");";
            var outcome = await executor.ExecuteAsync(document, uiApp, null, script, CancellationToken.None, exportsDir, overwriteOutputFiles: false);

            Assert.True(outcome.Success); // Publish never throws -- the script itself doesn't fail
            var failed = Assert.Single(outcome.Files);
            Assert.Equal(PublishedFileRecord.StatusFailed, failed.Status);
            Assert.NotNull(failed.Message);
            Assert.Empty(Directory.GetFiles(exportsDir)); // nothing copied, nothing falsely "published"
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ImportsAndExportsDirectories_AreBothPopulatedAndUsableFromAScript()
    {
        // Second-round independent review finding: production wiring (RequestDispatcher ->
        // ExecuteAsync -> ScriptGlobals) was correct, but had zero test coverage proving
        // ImportsDirectory/ExportsDirectory are actually populated and reachable by a real script --
        // this exercises both through the public script surface end-to-end.
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var tempDir = CreateTempDir();
        try
        {
            var exportsDir = Path.Combine(tempDir, "exports");
            var importsDir = Path.Combine(tempDir, "imports");
            Directory.CreateDirectory(exportsDir);
            Directory.CreateDirectory(importsDir);
            File.WriteAllText(Path.Combine(importsDir, "seed.txt"), "hello from imports");

            var script = "Connector.Publish(System.IO.Path.Combine(Connector.ImportsDirectory, \"seed.txt\"), \"from-imports.txt\");";
            var outcome = await executor.ExecuteAsync(document, uiApp, null, script, CancellationToken.None, exportsDir, importsDir, overwriteOutputFiles: false);

            Assert.True(outcome.Success);
            var published = Assert.Single(outcome.Files);
            Assert.Equal(PublishedFileRecord.StatusPublished, published.Status);
            var publishedPath = Path.Combine(exportsDir, "from-imports.txt");
            Assert.True(File.Exists(publishedPath));
            Assert.Equal("hello from imports", File.ReadAllText(publishedPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Issue #93: three members of the connector facade were forwarded but exercised only at tier 2, so
    /// the mutation <c>ExportsDirectory =&gt; _runtime.ImportsDirectory</c> passed the entire tier-1 suite.
    /// Forwarding is exactly the kind of thing that is boring to test and silently wrong when it breaks --
    /// a transposed pair of one-line properties compiles, ships, and reads correctly.
    ///
    /// <para><c>OpenForWriting</c> stays tier-2 by construction (it needs a real second Revit document);
    /// this covers the other two, plus the seam's cast, against the real script surface.</para>
    /// </summary>
    [Fact]
    public async Task ExportsAndImportsDirectories_ForwardToTheirOwnValues_NotEachOther()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();
        var tempDir = CreateTempDir();
        try
        {
            // Distinct directory names, so a transposed forward produces the WRONG string rather than a
            // coincidentally-equal one. Asserting on both in a single script means the test fails if
            // either is wired to the other.
            var exportsDir = Path.Combine(tempDir, "exports");
            var importsDir = Path.Combine(tempDir, "imports");
            Directory.CreateDirectory(exportsDir);
            Directory.CreateDirectory(importsDir);

            var outcome = await executor.ExecuteAsync(
                document, uiApp, null,
                "return Connector.ExportsDirectory + \"|\" + Connector.ImportsDirectory;",
                CancellationToken.None, exportsDir, importsDir, overwriteOutputFiles: false);

            Assert.True(outcome.Success);
            Assert.Equal($"{exportsDir}|{importsDir}", outcome.ReturnValue);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Issue #93, same gap: <c>DialogResultOverrides</c> is the dictionary a script mutates to change how
    /// a dialog is answered, and <c>TransactionScriptExecutor</c> must hand THAT SAME INSTANCE to
    /// <c>ActiveDialogContext</c>, which is what the dialog handler reads at runtime.
    ///
    /// <para>Asserted through <c>ActiveDialogContext</c> itself, not by reading the dictionary back in the
    /// script. Review caught the first version doing the latter: writing a key and reading it back proves
    /// only that the property returns a stable reference within one run, so replacing line 82 with
    /// <c>SetActive(new Dictionary&lt;string, int&gt;())</c> -- which silently kills every dialog override
    /// in the product -- left it green. The test name claimed the seam and tested the accessor.</para>
    ///
    /// <para>The observation point is a commit hook on the fake transaction, because the executor calls
    /// <c>CommitAll</c> inside its try block while the ambient context is still live; the finally that
    /// clears it has not run yet. A script cannot observe this itself -- <c>ActiveDialogContext</c> is
    /// internal precisely so it cannot (denylist round 3).</para>
    /// </summary>
    [Fact]
    public async Task DialogResultOverrides_WrittenByAScript_ReachesTheDictionaryTheExecutorPublished()
    {
        var executor = NewExecutor();
        var uiApp = new FakeUiApplicationAdapter();

        int? seenByTheDialogHandler = null;
        var document = new FakeDocumentAdapter();
        document.OnTransactionCommit = () =>
            seenByTheDialogHandler = ActiveDialogContext.TryGetOverride("TaskDialog_Probe");

        var outcome = await executor.ExecuteAsync(
            document, uiApp, null,
            "Connector.DialogResultOverrides[\"TaskDialog_Probe\"] = 1001;",
            CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(1001, seenByTheDialogHandler);
    }

    [Fact]
    public void CreatedDocumentsNotice_NamesEachCreatedDocumentWithItsIdAndACleanupRemedy()
    {
        // #122: the executor surfaces the ManagedDocumentTransactions identities as a §01 notice so a
        // created document that outlives its run never does so silently. (The full script->notice path
        // needs the real Revit Document type and is proven live in revit/test-harness; the notice shape and
        // the exposure are what tier 1 pins.)
        var created = new[]
        {
            new ManagedDocumentTransactions.CreatedDocumentRecord("Project1", "tmp-Project1"),
            new ManagedDocumentTransactions.CreatedDocumentRecord("Family1", "tmp-Family1"),
        };

        var notice = TransactionScriptExecutor.CreatedDocumentsNotice(created, orphanedByFailure: false);

        Assert.NotNull(notice);
        Assert.Equal("script-created-documents", notice!.Code);
        Assert.Contains("Project1", notice.Message);
        Assert.Contains("Family1", notice.Message);

        // The machine-readable handles the caller matches on.
        var docs = (System.Collections.Generic.Dictionary<string, object?>[])notice.Detail["created_documents"]!;
        Assert.Equal(2, docs.Length);
        Assert.Contains(docs, d => (string?)d["document_id"] == "tmp-Project1" && (string?)d["title"] == "Project1");
        Assert.Contains(docs, d => (string?)d["document_id"] == "tmp-Family1");

        // A handle is only useful with the way to act on it.
        Assert.Contains(notice.Remedy, r => r.Contains("confirm_lifecycle_actions"));
    }

    [Theory]
    // #122 (review): a created document is Info on a succeeded run (an intentional result) but a Warning
    // on a FAILED run (an orphan the agent never got to name -- the #114 leak), mirroring SettleNotice.
    [InlineData(false, DiagnosticSeverity.Info)]
    [InlineData(true, DiagnosticSeverity.Warning)]
    public void CreatedDocumentsNotice_SeverityReflectsWhetherTheRunFailed(bool orphanedByFailure, DiagnosticSeverity expected)
    {
        var created = new[] { new ManagedDocumentTransactions.CreatedDocumentRecord("Project1", "tmp-Project1") };

        var notice = TransactionScriptExecutor.CreatedDocumentsNotice(created, orphanedByFailure);

        Assert.NotNull(notice);
        Assert.Equal(expected, notice!.Severity);
        // The failure wording names the orphan situation; the success wording does not.
        Assert.Equal(orphanedByFailure, notice.Message.Contains("orphaned"));
    }

    [Fact]
    public void CreatedDocumentsNotice_ReturnsNull_WhenNothingWasCreated()
    {
        // The common case: no created documents -> no notice, so ordinary runs are not spammed.
        Assert.Null(TransactionScriptExecutor.CreatedDocumentsNotice(System.Array.Empty<ManagedDocumentTransactions.CreatedDocumentRecord>(), orphanedByFailure: false));
    }

    [Fact]
    public async Task ScriptThatDoesNotPublish_HasEmptyFilesArray_NoExportsDirectoryNeeded()
    {
        // exportsDirectoryPath omitted entirely -- existing callers/tests that don't pass one must
        // keep working unaffected by this feature.
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();

        var outcome = await executor.ExecuteAsync(document, uiApp, null, "1 + 1", CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Empty(outcome.Files);
    }

    [Fact]
    public async Task CreateProjectDocument_IsNotADenylistViolation()
    {
        // ISSUE #24's central claim, and the one thing about it that IS tier-1 checkable: the new
        // creation helper is an ordinary method call on ScriptGlobals, not an
        // Autodesk.Revit.DB.Transaction construction, so ScriptApiDenylist's check 1 -- which stays
        // completely unconditional and textually unchanged -- has nothing to bind to and does not fire.
        // Asserting it rather than assuming it, because the whole approach was chosen on the premise
        // that the denylist needs no narrowing.
        //
        // The run still FAILS in this tier: executing the call needs the real
        // Autodesk.Revit.DB.Document return type, and RevitAPI.dll is mixed-mode and unloadable outside
        // Revit (see RevitApiReference). That is fine -- the assertion is about WHICH failure, and the
        // create-and-write path proper is proven live in revit/test-harness.
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();

        var outcome = await executor.ExecuteAsync(
            document, uiApp, null, "Connector.CreateProjectDocument();", CancellationToken.None);

        Assert.IsNotType<ScriptApiDenylistViolationException>(outcome.Exception);
        // ALSO not a compile error, or this assertion would pass vacuously (independent PR review
        // finding): a typo in the script text above would fail to bind, produce a
        // CompilationErrorException, and satisfy the denylist assertion while proving nothing about the
        // denylist. Ruling that out is what makes this test say "the call COMPILED and check 1 did not
        // fire". The precise runtime exception is deliberately not asserted -- it comes from the JIT
        // failing to load mixed-mode RevitAPI.dll, which is an environment detail, not this claim.
        Assert.IsNotType<CompilationErrorException>(outcome.Exception);
    }

    [Fact]
    public async Task CreateFamilyDocument_IsNotADenylistViolation()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();

        var outcome = await executor.ExecuteAsync(
            document, uiApp, null, "Connector.CreateFamilyDocument(@\"C:\\t.rft\");", CancellationToken.None);

        Assert.IsNotType<ScriptApiDenylistViolationException>(outcome.Exception);
        Assert.IsNotType<CompilationErrorException>(outcome.Exception);
    }

    [Fact]
    public async Task EveryDocumentIsRolledBack_WhenTheRunnerItselfThrows()
    {
        // The self-review fix that moved RollBackAll() into ExecuteAsync's `finally` had no
        // executor-level test -- only the two unit properties that ENABLE it (RollBackAll's
        // idempotence, and CommitAll leaving the set empty). This closes that, and the seam is real
        // rather than manufactured: RoslynScriptRunner takes an alcFactory, and it is invoked BEFORE
        // RunAsync's own try block, so a throwing factory makes RunAsync throw instead of returning a
        // failed outcome -- exactly the shape the `finally` exists for. Without it the ambient
        // document's Transaction and TransactionGroup are left open in the live Revit session.
        var executor = new TransactionScriptExecutor(new RoslynScriptRunner(
            alcFactory: () => throw new InvalidOperationException("simulated load-context failure"),
            additionalMetadataReferences: RevitApiReference.References));
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(document, uiApp, null, "1 + 1", CancellationToken.None));

        Assert.Equal(new[] { "Start", "RollBack", "Dispose" }, document.LastTransaction!.Calls);
        Assert.Equal(new[] { "Start", "RollBack", "Dispose" }, document.LastTransactionGroup!.Calls);
    }

    [Fact]
    public async Task ConstructingATransaction_IsStillRefused_EvenAgainstACreatedDocument()
    {
        // The other half of the same claim: nothing about issue #24 loosened check 1. A script may
        // still not open its own Transaction, whatever document it names -- including one it created
        // itself, which is exactly the case the connector now covers on the script's behalf.
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();

        var outcome = await executor.ExecuteAsync(
            document,
            uiApp,
            null,
            "var d = Connector.CreateProjectDocument(); new Autodesk.Revit.DB.Transaction(d, \"x\");",
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.IsType<ScriptApiDenylistViolationException>(outcome.Exception);
    }

    /// <summary>Test-only helper: a document adapter that hands out a pre-built (rigged) transaction instead of a fresh one.</summary>
    private sealed class RiggedDocumentAdapter : MCPBridge.RevitAdapter.IDocumentAdapter
    {
        private readonly FakeDocumentAdapter _inner;
        private readonly FakeTransactionAdapter _riggedTransaction;

        public RiggedDocumentAdapter(FakeDocumentAdapter inner, FakeTransactionAdapter riggedTransaction)
        {
            _inner = inner;
            _riggedTransaction = riggedTransaction;
        }

        public string Title => _inner.Title;
        public string? PathName => _inner.PathName;
        public bool IsWorkshared => _inner.IsWorkshared;
        public string? CentralModelPath => _inner.CentralModelPath;
        public string DocumentId => _inner.DocumentId;

        public MCPBridge.RevitAdapter.ITransactionAdapter CreateTransaction(string name) => _riggedTransaction;

        public FakeTransactionGroupAdapter? LastTransactionGroup { get; private set; }

        public MCPBridge.RevitAdapter.ITransactionGroupAdapter CreateTransactionGroup(string name)
        {
            LastTransactionGroup = new FakeTransactionGroupAdapter(name);
            return LastTransactionGroup;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The settle notice (issue #132, decision 2).
    //
    // Tested as a MAPPING rather than end-to-end, and that is a real limit rather than a shortcut:
    // reaching Connector.Settle from a script requires IExistingDocumentSource, which only the live
    // adapter implements (Autodesk.Revit.DB.Document cannot be wrapped outside a running Revit), so a
    // fake genuinely cannot get there. That the EXECUTOR emits these on both the success and failure
    // paths belongs in the tier-2 harness; what is checkable here is that the record an agent receives
    // says the right thing.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SettleNotice_ForKeptChanges_WarnsThatRollbackIsNoLongerPossible()
    {
        var notice = TransactionScriptExecutor.SettleNotice(
            new ManagedDocumentTransactions.SettlementRecord("work (active document)", kept: true));

        Assert.Equal(DiagnosticSeverity.Warning, notice.Severity);
        Assert.Equal("document-settled-kept", notice.Code);
        Assert.Contains("work (active document)", notice.Message);
        Assert.Contains("now permanent", notice.Message);
        // The consequence, not just the event -- an agent has to know the run's rollback guarantee
        // no longer covers this document.
        Assert.Contains("can no longer undo", notice.Message);
        Assert.NotEmpty(notice.Remedy);
    }

    [Fact]
    public void SettleNotice_ForDiscardedChanges_SaysSoAndOffersNoRemedy()
    {
        var notice = TransactionScriptExecutor.SettleNotice(
            new ManagedDocumentTransactions.SettlementRecord("scratch", kept: false));

        Assert.Equal(DiagnosticSeverity.Warning, notice.Severity);
        Assert.Equal("document-settled-discarded", notice.Code);
        Assert.Contains("discarded", notice.Message);
        // No remedy, deliberately: discarding is what the script asked for, and a remedy on an
        // intentional outcome trains an agent to ignore the field. Asserted EMPTY rather than null --
        // DiagnosticRecord.Create normalises a null remedy to an empty array, so Assert.Null passes
        // only for a record that never went through Create.
        Assert.Empty(notice.Remedy);
    }
}
