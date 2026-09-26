using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace DesktopShell;

public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            RequestedThemeVariant = ToVariant(ThemeSettings.Load());

            MainWindow window = new();
            desktop.MainWindow = window;
            if (desktop.Args is ["--smoke-test", var smokeDirectory])
            {
                LaunchEvidence.CaptureAfterOpen(window, desktop, smokeDirectory);
            }
            else if (desktop.Args is ["--workflow-capture", var workflowDirectory])
            {
                LaunchEvidence.CaptureWorkflow(window, desktop, workflowDirectory);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static ThemeVariant ToVariant(string theme) => theme switch
    {
        "Light" => ThemeVariant.Light,
        "Dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };
}