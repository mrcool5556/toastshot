using System.Windows;
using System.Windows.Threading;
using Glance.Services;

namespace Glance;

public partial class App : Application
{
    private const string MutexName = @"Local\Glance.SingleInstance";
    private const string ActivateEventName = @"Local\Glance.Activate";

    private Mutex? _singleInstance;
    private EventWaitHandle? _activate;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (!isFirst)
        {
            // Already running: bring the existing window forward instead of starting twice.
            try
            {
                using var other = EventWaitHandle.OpenExisting(ActivateEventName);
                other.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }

            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        ThemeManager.Apply(this, ThemeManager.SystemTheme());
        ThemeManager.FollowSystem(this);
        TempFiles.CleanUp(TimeSpan.FromDays(1));

        _controller = new AppController(AppSettings.Load());
        _controller.Start(e.Args);

        // A second launch signals this event; show the window instead of running twice.
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) =>
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                Dispatcher.BeginInvoke(() => _controller?.ShowMain());
            }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private RegisteredWaitHandle? _activateWait;

    protected override void OnExit(ExitEventArgs e)
    {
        _activateWait?.Unregister(null);
        ThemeManager.StopFollowingSystem();
        _controller?.Dispose();
        _activate?.Dispose();
        if (_singleInstance is not null)
        {
            _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
        }

        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep running: one failed action shouldn't take down the tray app.
        MessageBox.Show(e.Exception.Message, "Glance", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
