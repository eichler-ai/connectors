using Xunit;

// RoslynScriptRunner redirects the process-wide Console.Out around every script run (to capture
// stdout) and restores it afterwards -- including for tests, like TransactionScriptExecutorTests,
// that don't print anything themselves but still go through the same RunAsync path. xUnit runs
// different test classes in parallel by default; since Console.Out is shared static state, two
// classes doing that redirection concurrently can race and clobber each other's capture (surfaced
// as a flaky, non-deterministic failure in RoslynScriptRunnerTests.RunAsync_CapturesStdOut -- see
// the phase-01/PR #2 review notes). Disabling cross-class parallelization for this assembly is the
// correct fix for a shared-global-state race, not a symptom-hiding workaround: the suite is small
// (under 100 tests, ~2s), so the cost is negligible.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
