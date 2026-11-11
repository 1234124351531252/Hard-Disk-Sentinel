using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DiskSentinel;

public partial class App : System.Windows.Application
{
    private const string InstanceMutexName = "Local\\DiskSentinel-9D631B6D-58D5-4DBE-8C79-6D465E22E4C2";
    private const string ActivateEventName = "Local\\DiskSentinel-Activate-9D631B6D-58D5-4DBE-8C79-6D465E22E4C2";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _activationCancellation;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        _ownsMutex = true;
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        _activationCancellation = new CancellationTokenSource();
        var token = _activationCancellation.Token;
        _ = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try { if (!_activateEvent!.WaitOne(500)) continue; }
                catch (ObjectDisposedException) { break; }
                Dispatcher.InvokeAsync(() =>
                {
                    if (MainWindow is MainWindow mainWindow) mainWindow.ActivateFromSecondInstance();
                });
            }
        }, token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationCancellation?.Cancel();
        _activateEvent?.Dispose();
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void SignalExistingInstance()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var activateEvent = EventWaitHandle.OpenExisting(ActivateEventName);
                activateEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { return; }
        }
    }
}
