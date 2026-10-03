using System;
using System.Threading;
using System.Threading.Tasks;
using Blossom;

namespace TGK.Client;

/// <summary>
/// Marshalling to the UI (GLFW main) thread. <see cref="Install"/> also sets a <see cref="SynchronizationContext"/>
/// on it, so an <c>await</c> started on the UI thread resumes there, already while the application is built (Blossom
/// installs its own, equivalent one only once the window has loaded).
/// </summary>
public static class UiThread
{
    private static int _uiThreadId = -1;

    public static bool IsCurrent => Environment.CurrentManagedThreadId == _uiThreadId;

    /// <summary>Call once from the UI thread before <c>Shell.Initialize</c>.</summary>
    public static void Install()
    {
        _uiThreadId = Environment.CurrentManagedThreadId;
        SynchronizationContext.SetSynchronizationContext(new UiSynchronizationContext());
    }

    /// <summary>Queues <paramref name="action"/> to run on the UI thread (thread-safe).</summary>
    public static void Post(Action action) => Shell.Post(action);

    /// <summary>Runs <paramref name="func"/> on the UI thread and returns its result, e.g. to show a dialog from a worker thread.</summary>
    public static Task<T> InvokeAsync<T>(Func<Task<T>> func)
    {
        if (IsCurrent)
            return func();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Shell.Post(async () =>
        {
            try
            {
                tcs.TrySetResult(await func());
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private sealed class UiSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => Shell.Post(() => d(state));

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (IsCurrent)
            {
                d(state);
                return;
            }
            using var done = new ManualResetEventSlim();
            Shell.Post(() =>
            {
                try
                {
                    d(state);
                }
                finally
                {
                    done.Set();
                }
            });
            done.Wait();
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
