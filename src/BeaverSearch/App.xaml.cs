using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace BeaverSearch;

public partial class App : Application
{
    private static readonly object LogGate = new();
    private static string StartupLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BeaverSearch", "startup.log");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        SplashWindow? splash = null;
        try
        {
            WriteStartupLog("Application startup begin.");

            splash = new SplashWindow();
            splash.Show();
            await splash.PlaySequenceAsync();

            // Construct the MainWindow while the splash is still visible. If XAML/runtime
            // initialization fails, the user sees an error instead of a blank transition.
            WriteStartupLog("Constructing MainWindow.");
            var window = new MainWindow();
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            WriteStartupLog("MainWindow shown successfully.");

            await splash.FadeOutAsync();
            splash.Close();
            splash = null;
        }
        catch (Exception ex)
        {
            ReportFatalStartupException("Application startup", ex);
            try { splash?.Close(); } catch { }
            Shutdown(-1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteException("DispatcherUnhandledException", e.Exception);
        MessageBox.Show(
            "BeaverSearch столкнулся с ошибкой во время работы.\n\n" +
            e.Exception.Message + "\n\nПолный лог:\n" + StartupLogPath,
            "BeaverSearch — runtime error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) WriteException("AppDomain.UnhandledException", ex);
        else WriteStartupLog("AppDomain.UnhandledException: " + e.ExceptionObject);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteException("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    public static void ReportFatalStartupException(string stage, Exception ex)
    {
        WriteException(stage, ex);
        try
        {
            MessageBox.Show(
                "BeaverSearch не смог завершить запуск.\n\n" +
                $"Этап: {stage}\n" +
                $"Ошибка: {ex.Message}\n\n" +
                "Полный технический лог сохранён сюда:\n" + StartupLogPath,
                "BeaverSearch — ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // Last resort: logging must not cause another crash.
        }
    }

    public static void LogStartupStage(string message) => WriteStartupLog(message);

    private static void WriteException(string stage, Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {stage}");
        sb.AppendLine(ex.ToString());
        WriteStartupLog(sb.ToString());
    }

    private static void WriteStartupLog(string message)
    {
        try
        {
            lock (LogGate)
            {
                var folder = Path.GetDirectoryName(StartupLogPath)!;
                Directory.CreateDirectory(folder);
                File.AppendAllText(StartupLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Startup logging is best-effort only.
        }
    }
}
