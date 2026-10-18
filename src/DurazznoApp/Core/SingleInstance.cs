namespace Durazzno.Core;

/// <summary>
/// One Durazzno per user session. Launching the exe again just asks the running copy to open its window.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\DurazznoApp.SingleInstance";
    private const string ActivateEventName = @"Local\DurazznoApp.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
    }

    /// <summary>Returns null when another instance already owns the session.</summary>
    public static SingleInstance? TryAcquire(TimeSpan wait)
    {
        var mutex = new Mutex(false, MutexName);
        try
        {
            if (!mutex.WaitOne(wait))
            {
                mutex.Dispose();
                return null;
            }
        }
        catch (AbandonedMutexException)
        {
            // Previous instance was killed; the mutex is ours now.
        }

        return new SingleInstance(mutex);
    }

    public static void SignalRunningInstance()
    {
        if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var handle))
        {
            using (handle)
                handle.Set();
        }
    }

    /// <summary>Runs <paramref name="onActivate"/> on the captured UI context whenever a second launch happens.</summary>
    public void ListenForActivation(Action onActivate)
    {
        var context = SynchronizationContext.Current ?? new SynchronizationContext();
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activate, (_, _) => context.Post(_ => onActivate(), null), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activate.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
