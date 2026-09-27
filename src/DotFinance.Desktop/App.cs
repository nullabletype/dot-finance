using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace DotFinance.Desktop;

internal sealed class App(string? evidenceDirectory) : Avalonia.Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new Window
            {
                Title = "dot-finance",
                Width = 840,
                Height = 520,
                MinWidth = 480,
                MinHeight = 320,
                Content = new StackPanel
                {
                    Margin = new Thickness(48),
                    Spacing = 20,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = "dot-finance", FontSize = 18, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = "Your finances. On your device.", FontSize = 32, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = "Reconcile spending and keep track of contributions, privately.", FontSize = 16, TextWrapping = TextWrapping.Wrap }
                    }
                }
            };

            if (evidenceDirectory is not null)
            {
                window.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    Directory.CreateDirectory(evidenceDirectory);
                    var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
                    using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(evidenceDirectory, "window.png"), PngBitmapEncoderOptions.Default);
                    File.WriteAllText(Path.Combine(evidenceDirectory, "opened.txt"),
                        Environment.GetEnvironmentVariable("DOT_FINANCE_SMOKE_TOKEN") ?? "manual");
                    desktop.Shutdown();
                }, DispatcherPriority.ApplicationIdle);
            }

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}