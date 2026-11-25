using System.Threading;
using System.Windows;

namespace CapriDesktop;

/// <summary>One Capri at a time: started again (her desktop icon double-clicked while she's up), the new one only
/// tells the one already running, which zaps where she stands to show she's there, and quits. Test runs (--demo)
/// don't count.</summary>
public partial class App : Application
{
    const string OnlyOne = "CapriDesktop.OnlyOne", Wake = "CapriDesktop.Wake";
    Mutex? _only;

    /// <summary>Set when another start asks her to show herself.</summary>
    public static EventWaitHandle? WakeSignal { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // a test of a brand-new install: its own empty settings, beside any Capri already running
        var fresh = e.Args.Contains("--fresh");
        if (fresh) Settings.Folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CapriDesktop-fresh-" + DateTime.Now.Ticks);
        if (!e.Args.Contains("--demo") && !fresh)
        {
            _only = new Mutex(true, OnlyOne, out var first);
            if (!first)
            {
                try { EventWaitHandle.OpenExisting(Wake).Set(); } catch (Exception) { /* she's still starting up */ }
                Shutdown();
                return;
            }
            WakeSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Wake);
        }
        MainWindow = new MainWindow();
        try { MainWindow.Show(); }
        catch (InvalidOperationException) { Shutdown(); }   // (she closed herself: no art found)
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _only?.ReleaseMutex();
        _only?.Dispose();
        base.OnExit(e);
    }
}
