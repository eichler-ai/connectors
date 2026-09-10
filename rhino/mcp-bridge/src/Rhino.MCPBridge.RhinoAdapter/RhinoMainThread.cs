namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>RhinoApp.InvokeOnUiThread with a bounded wait. Calling from the main thread itself runs
/// inline (InvokeOnUiThread would otherwise deadlock against our own wait). A main thread that does not
/// pick the work up within <see cref="Timeout"/> -- a modal, a long command, a solve -- surfaces as a
/// <see cref="TimeoutException"/> rather than a caller blocked forever (review of #281); the queued
/// work still runs later, so callers must not assume it was dropped.</summary>
internal sealed class RhinoMainThread : IMainThread
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly int _mainThreadId;

    public RhinoMainThread(int mainThreadId)
    {
        _mainThreadId = mainThreadId;
    }

    public T Invoke<T>(Func<T> func)
    {
        if (Environment.CurrentManagedThreadId == _mainThreadId)
        {
            return func();
        }

        T result = default!;
        Exception? error = null;
        using var done = new ManualResetEventSlim(false);
        RhinoApp.InvokeOnUiThread(() =>
        {
            try { result = func(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        if (!done.Wait(Timeout))
        {
            throw new TimeoutException($"Rhino's main thread did not run queued work within {Timeout.TotalSeconds}s (a modal dialog, the template chooser, or a long command is holding it)");
        }

        if (error is not null)
        {
            // Rethrow the ORIGINAL so typed exceptions (a capture refusal, document-not-found) keep
            // their type and record across the thread hop; a wrapper turned every one into a generic
            // capture-failed (found live).
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        return result;
    }

    public void Invoke(Action action) => Invoke(() => { action(); return 0; });
}
