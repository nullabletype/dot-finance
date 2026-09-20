using Avalonia;

namespace DesktopShell;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--persistence-cycle", ..])
        {
            return PersistenceScenario.RunCommand(args);
        }

        return AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}
