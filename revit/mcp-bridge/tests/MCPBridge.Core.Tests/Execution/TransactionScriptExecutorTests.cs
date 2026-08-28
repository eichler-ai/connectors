using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MCPBridge.Core.Execution;
using MCPBridge.Core.Tests.Fakes;
using MCPBridge.RevitAdapter;
using Xunit;

namespace MCPBridge.Core.Tests.Execution;

public class TransactionScriptExecutorTests
{
    // RevitAPI/RevitAPIUI are supplied as METADATA references, not loaded assemblies -- ScriptGlobals'
    // members are real Revit types as of PRD §14, so without them nothing here would bind. See
    // RevitApiReference's doc comment for why they cannot simply be loaded.
    private static TransactionScriptExecutor NewExecutor() =>
        new(new RoslynScriptRunner(additionalMetadataReferencePaths: RevitApiReference.Paths));

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
        Assert.Equal(new[] { "Start", "Commit" }, document.LastTransaction!.Calls);
        Assert.Equal(new[] { "Start", "Assimilate" }, document.LastTransactionGroup!.Calls);
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
        Assert.Equal(new[] { "Start", "RollBack" }, document.LastTransaction!.Calls);
        Assert.Equal(new[] { "Start", "RollBack" }, document.LastTransactionGroup!.Calls);
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
        Assert.Equal(new[] { "Start", "RollBack" }, document.LastTransaction!.Calls);
        Assert.Equal(new[] { "Start", "RollBack" }, document.LastTransactionGroup!.Calls);
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
        Assert.Equal(new[] { "Start", "RollBack" }, document.LastTransaction!.Calls);
        Assert.Equal(new[] { "Start", "RollBack" }, document.LastTransactionGroup!.Calls);
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
        Assert.Equal(new[] { "Start", "Commit", "RollBack" }, transaction.Calls);
        Assert.Equal(new[] { "Start", "RollBack" }, riggedDocument.LastTransactionGroup!.Calls);
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
        Assert.Equal(new[] { "Start", "RollBack" }, document.LastTransaction!.Calls);
        Assert.Equal(new[] { "Start", "RollBack" }, document.LastTransactionGroup!.Calls);
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
        Assert.Equal(new[] { "Start", "Commit" }, transaction.Calls);
        Assert.Equal(new[] { "Start", "Assimilate" }, riggedDocument.LastTransactionGroup!.Calls);
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
        Assert.Equal(new[] { "Start", "Commit" }, transaction.Calls);
        Assert.Equal(new[] { "Start", "RollBack" }, riggedDocument.LastTransactionGroup!.Calls);
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

            var script = $"Publish(@\"{sourcePath}\");";
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

            var script = $"Publish(@\"{sourcePath}\");";
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

            var script = $"Publish(@\"{sourcePath}\");";
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

            var script = $"Publish(@\"{sourcePath}\"); throw new System.InvalidOperationException(\"boom\");";
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
            // Instead, the script itself calls Publish() and then throws OperationCanceledException
            // directly, which RunAsync catches and reports as WasCancelled -- exercising the exact
            // "published, then the run resolved to cancelled" sequence PRD §09's invariant describes,
            // without depending on a real mid-run cooperative-cancellation race.
            var script = $"Publish(@\"{sourcePath}\"); throw new System.OperationCanceledException();";
            var outcome = await executor.ExecuteAsync(document, uiApp, null, script, CancellationToken.None, exportsDir, overwriteOutputFiles: false);

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

            var script = $"Publish(@\"{sourceA}\"); Publish(@\"{sourceB}\");";
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

            var script = $"System.IO.File.WriteAllText(@\"{alreadyThere}\", \"hi\"); Publish(@\"{alreadyThere}\");";
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

            var script = $"Publish(@\"{sourcePath}\", \"renamed.png\");";
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

            var script = $"Publish(@\"{sourcePath}\", @\"{escapeAttempt}\");";
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

            var script = $"Publish(@\"{missingSource}\");";
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

            var script = $"Publish(@\"{directoryAsSource}\");";
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

            var script = $"Publish(@\"{sourcePathWithTrailingSeparator}\");";
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

            var script = "Publish(System.IO.Path.Combine(ImportsDirectory, \"seed.txt\"), \"from-imports.txt\");";
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
            document, uiApp, null, "CreateProjectDocument();", CancellationToken.None);

        Assert.IsNotType<ScriptApiDenylistViolationException>(outcome.Exception);
    }

    [Fact]
    public async Task CreateFamilyDocument_IsNotADenylistViolation()
    {
        var executor = NewExecutor();
        var document = new FakeDocumentAdapter();
        var uiApp = new FakeUiApplicationAdapter();

        var outcome = await executor.ExecuteAsync(
            document, uiApp, null, "CreateFamilyDocument(@\"C:\\t.rft\");", CancellationToken.None);

        Assert.IsNotType<ScriptApiDenylistViolationException>(outcome.Exception);
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
            "var d = CreateProjectDocument(); new Autodesk.Revit.DB.Transaction(d, \"x\");",
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
}
