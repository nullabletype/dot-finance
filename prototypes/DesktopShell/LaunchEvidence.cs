using System.Runtime.InteropServices;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace DesktopShell;

internal static class LaunchEvidence
{
    public static void CaptureAfterOpen(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop, string outputDirectory)
    {
        window.Opened += (_, _) => DispatcherTimer.RunOnce(() =>
        {
            try
            {
                if (!window.IsVisible || window.Bounds.Width <= 0 || window.Bounds.Height <= 0)
                {
                    throw new InvalidOperationException("A visible native window is required for launch evidence.");
                }

                Directory.CreateDirectory(outputDirectory);
                using RenderTargetBitmap image = new(
                    new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
                image.Render(window);
                image.Save(Path.Combine(outputDirectory, "window.png"), PngBitmapEncoderOptions.Default);
                File.WriteAllText(Path.Combine(outputDirectory, "launch.json"), JsonSerializer.Serialize(new
                {
                    commit = Environment.GetEnvironmentVariable("PROTOTYPE_COMMIT"),
                    os = RuntimeInformation.OSDescription,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    framework = RuntimeInformation.FrameworkDescription,
                    windowOpened = true,
                    width = window.Bounds.Width,
                    height = window.Bounds.Height,
                    timestampUtc = DateTimeOffset.UtcNow,
                    scope = "Native-window launch and control-tree rendering only; not full workflow acceptance."
                }));
                desktop.Shutdown(0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                desktop.Shutdown(1);
            }
        }, TimeSpan.FromSeconds(2));
    }
}