using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ClaudeSessions;

public partial class App : Application
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "ccsessions.log");

    public static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}"); }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // A transcript can vanish mid-read when a distro stops; never take the app down for it.
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log("FATAL: " + args.ExceptionObject);
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("UNHANDLED: " + e.Exception);
        MessageBox.Show(e.Exception.ToString(), "Claude Session History",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
