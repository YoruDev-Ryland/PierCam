using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PierCam;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PierCam", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        // An unattended camera app should never die silently overnight: log everything that
        // escapes, and keep the window up if the failure was survivable.
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log(args.ExceptionObject as Exception, "AppDomain");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(args.Exception, "Task");
            args.SetObserved();
        };

        // Publish the shared noise tile once. Every frosted panel references it by key, so
        // the whole frosted-glass effect costs one 16 KB frozen bitmap for the process.
        Resources["GrainBrush"] = Ui.Controls.Grain.Brush;

        base.OnStartup(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception, "Dispatcher");
        MessageBox.Show(
            $"{e.Exception.Message}\n\nPierCam will keep running. Details were written to:\n{LogPath}",
            "PierCam hit a problem", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    public static void Log(Exception? ex, string source)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Records something that happened rather than something that went wrong. Housekeeping runs
    /// unattended and rewrites finished videos, so there has to be a record of what it did
    /// somewhere you can read the morning after.
    /// </summary>
    public static void Note(string message, string source = "PierCam")
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static string CrashLogPath => LogPath;

    /// <summary>Theme switching lives in <see cref="Ui.ThemeManager"/>.</summary>
    public static void ApplyTheme(string? themeId) => Ui.ThemeManager.Apply(themeId);
}
