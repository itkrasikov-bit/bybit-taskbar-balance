using System.Windows;
using System.Windows.Threading;
using BybitTaskbar.Core;

namespace BybitTaskbar;

public partial class App : Application
{
    private const string MutexName = @"Local\BybitTaskbar.SingleInstance";
    private const string ActivateEventName = @"Local\BybitTaskbar.Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private bool _ownsMutex;

    public static AppConfig Config { get; set; } = new();
    public static bool DemoMode { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DemoMode = e.Args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(true, MutexName, out var createdNew);
        _ownsMutex = createdNew;

        if (!createdNew)
        {
            // Уже запущено — просто показываем существующий виджет.
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ActivateEventName);
                ev.Set();
            }
            catch (Exception ex)
            {
                AppLog.Write("activate existing failed: " + ex.Message);
            }

            Shutdown();
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateEvent.Reset();
        var waiter = new Thread(ActivationLoop) { IsBackground = true, Name = "BybitTaskbar.Activation" };
        waiter.Start();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Write("fatal: " + args.ExceptionObject);

        Config = AppConfig.Load();

        var window = new MainWindow(DemoMode);
        MainWindow = window;
        window.Show();
    }

    private void ActivationLoop()
    {
        while (true)
        {
            try
            {
                if (_activateEvent?.WaitOne() != true) return;
                Dispatcher.Invoke(() =>
                {
                    if (MainWindow is MainWindow w)
                        w.Reveal();
                });
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLog.Write("activation loop: " + ex.Message);
                return;
            }
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write("UI exception: " + e.Exception);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Config.Save();
        }
        catch
        {
            // ignore
        }

        if (_ownsMutex)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch
            {
                // ignore
            }
        }

        _mutex?.Dispose();
        _activateEvent?.Dispose();
        base.OnExit(e);
    }
}
