namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>RhinoApp.InvokeOnUiThread with a wait. Calling from the main thread itself runs inline
/// (InvokeOnUiThread would otherwise deadlock against our own wait).</summary>
internal sealed class RhinoMainThread : IMainThread
{
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
        done.Wait();
        if (error is not null)
        {
            throw new InvalidOperationException("main-thread work failed: " + error.Message, error);
        }

        return result;
    }

    public void Invoke(Action action) => Invoke(() => { action(); return 0; });
}
