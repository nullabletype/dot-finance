using Avalonia;

namespace DesktopShell;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => args switch
    {
        ["--persistence-cycle", ..] => PersistenceScenario.RunCommand(args),
        ["--persistence-interrupt", ..] => PersistenceScenario.RunInterruptionCommand(args),
        ["--persistence-check-recovery", ..] => PersistenceScenario.RunRecoveryCheckCommand(args),
        _ => AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args)
    };
}