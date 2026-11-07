using System;
using System.IO;
using Avalonia;

namespace EasyFileSync.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine();
            Console.Error.WriteLine("================ CRASH ERROR ================");
            Console.Error.WriteLine(ex.ToString());
            Console.Error.WriteLine("=============================================");
            Console.ResetColor();

            try
            {
                string log = Path.Combine(Path.GetTempPath(), "EasyFileSync-crash.log");
                File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
            }
            catch
            {
            }

            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
