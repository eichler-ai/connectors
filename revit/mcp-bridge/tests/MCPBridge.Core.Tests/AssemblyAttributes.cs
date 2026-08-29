// Cross-class test parallelization is ENABLED (xunit's default) as of issue #52. This file used to
// carry [assembly: CollectionBehavior(DisableTestParallelization = true)] because RoslynScriptRunner
// swapped the process-wide Console.Out around every run -- a shared-global-state race that surfaced
// as flaky stdout captures when classes ran in parallel (phase-01/PR #2 review notes). That swap is
// gone: ScriptConsoleCapture's AsyncLocal ambient writer is per-execution-context, so concurrent
// runs -- including parallel test classes -- capture independently by construction (pinned by
// RoslynScriptRunnerTests.RunAsync_ConcurrentRuns_CaptureTheirOwnStdOut_Independently). The suite
// grew well past the "under 100 tests, ~2s" this attribute's original comment assumed (~400 tests,
// ~60s serial), so the parallelism is now worth collecting; if a new process-global sneaks in and
// flakes the suite, fix the global (CONVENTIONS.md's script-reachable/state rules) rather than
// re-disabling parallelism for everyone.
