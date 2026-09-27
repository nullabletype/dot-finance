using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => AppBuilder.Configure<ProbeApp>()
        .UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

internal sealed class ProbeApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new Window
            {
                Title = "dot-finance single-executable feasibility probe",
                Width = 500,
                Height = 200,
                Content = new TextBlock { Text = "Synthetic foundation probe" }
            };
            window.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                Console.WriteLine("PASS: native window opened");
                desktop.Shutdown();
            }, DispatcherPriority.ApplicationIdle);
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}