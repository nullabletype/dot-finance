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

        if (args is ["--persistence-interrupt", ..])
        {
            return PersistenceScenario.RunInterruptionCommand(args);
        }

        if (args is ["--persistence-check-recovery", ..])
        {
            return PersistenceScenario.RunRecoveryCheckCommand(args);
        }

        return AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}
