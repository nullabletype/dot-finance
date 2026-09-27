using Avalonia;

namespace DotFinance.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--smoke-test"))
        {
            return 2;
        }

        string? evidenceDirectory = args.Length == 2 ? args[1] : null;
        return AppBuilder.Configure(() => new App(evidenceDirectory))
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
    }
}