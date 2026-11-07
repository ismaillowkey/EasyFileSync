using System;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using EasyFileSync.App.Views;
using EasyFileSync.Core.Engine;

namespace EasyFileSync.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) => LogCrash(e.Exception);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = new AppPaths();
            var store = new JobStore(paths);
            var state = new StateStore(paths);
            var engine = new SyncEngine(store, state);
            var auto = new AutoRunner(store, engine);
            var logWriter = new JobLogWriter(paths);

            var window = new MainWindow(store, engine, auto);

            auto.Reload();
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) =>
            {
                auto.Dispose();
                logWriter.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            if (ex != null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine();
                Console.Error.WriteLine("================ EasyFileSync UNHANDLED ERROR ================");
                Console.Error.WriteLine(ex.ToString());
                Console.Error.WriteLine("=========================================================");
                Console.ResetColor();
            }

            var log = Path.Combine(Path.GetTempPath(), "EasyFileSync-crash.log");
            File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
        }
    }
}
