using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace DesktopShell;

public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindow window = new();
            desktop.MainWindow = window;
            if (desktop.Args is ["--smoke-test", var outputDirectory])
            {
                LaunchEvidence.CaptureAfterOpen(window, desktop, outputDirectory);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}