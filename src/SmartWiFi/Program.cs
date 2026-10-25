using SmartWiFi.Platform;
using SmartWiFi.UI;

namespace SmartWiFi;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // One copy per Windows user.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\SmartWiFi.SingleInstance", out var first);
        if (!first)
        {
            if (!args.Contains("--background"))
                MessageBox.Show("SmartWiFi is already running. Look for its icon in the tray, next to the clock (you may need to click ^).",
                    "SmartWiFi", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Crash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Crash(e.Exception); e.SetObserved(); };

        using var app = new TrayApp(background: args.Contains("--background"));
        Application.Run(app);
        return 0;
    }

    private static void Crash(Exception? e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.AppendAllText(Path.Combine(AppPaths.Data, "crash.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { }
    }
}
