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

    private readonly System.Collections.Generic.HashSet<string> _shownErrors = new();
    private bool _showingError;

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception, "Dispatcher");
        e.Handled = true;

        // Once per distinct failure per session. The log records every occurrence, but a fault
        // that repeats on every click - a save that cannot succeed, say - must not become a
        // dialog on every click. And never a second dialog over the first: showing one runs a
        // nested message loop, which is exactly where the next occurrence would arrive.
        var key = e.Exception.GetType().FullName + ": " + e.Exception.Message;
        if (_showingError || !_shownErrors.Add(key)) return;

        _showingError = true;
        try
        {
            var message = $"{e.Exception.Message}\n\nPierCam will keep running. " +
                          $"Details were written to:\n{LogPath}";

            // The app's own dialog whenever there is a visible window to own it. A dialog owned
            // by a minimised window is hidden along with it while still blocking input, and
            // before the main window has been shown there is nothing to own it at all - in those
            // cases the system box is the only thing that can actually appear.
            if (MainWindow is { IsLoaded: true, IsVisible: true } owner &&
                owner.WindowState != WindowState.Minimized)
            {
                Ui.Dialogs.Alert(owner, "PierCam hit a problem", message);
            }
            else
            {
                MessageBox.Show(message, "PierCam hit a problem",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            Log(ex, "ErrorDialog");
        }
        finally
        {
            _showingError = false;
        }
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
