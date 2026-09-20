using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DesktopShell;

public sealed class MainWindow : Window
{
    public MainWindow()
    {
        Title = "dot-finance · Desktop prototype";
        Width = 960;
        Height = 660;
        MinWidth = 640;
        MinHeight = 540;

        StackPanel summary = new() { Spacing = 18, Margin = new Thickness(36) };
        summary.Children.Add(new TextBlock { Text = "DOT-FINANCE / EXPERIMENT 02", FontSize = 14 });
        StackPanel heading = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
        heading.Children.Add(CreateIcon("M4 5H20V19H4Z M4 9H20 M8 14H12", 32));
        heading.Children.Add(new TextBlock { Text = "Reconciliation overview", FontSize = 32, FontWeight = FontWeight.SemiBold });
        summary.Children.Add(heading);
        summary.Children.Add(new TextBlock
        {
            Text = "Synthetic demonstration · No financial data loaded",
            FontSize = 16
        });
        summary.Children.Add(new Separator());
        summary.Children.Add(Row("M3 5H21V19H3Z M3 10H21 M6 15H10", "Card payment target", "£1,200.00"));
        summary.Children.Add(Row("M9 10A3 3 0 1 0 9 4A3 3 0 1 0 9 10 M2 20V17Q2 13 9 13Q16 13 16 17V20 M17 4Q22 4 22 7Q22 10 18 10 M19 13Q23 14 23 18V20", "Joint contribution", "£450.00"));
        summary.Children.Add(Row("M3 9L12 3L21 9Z M5 11V18 M10 11V18 M14 11V18 M19 11V18 M3 21H21", "Shared Savings contribution", "£250.00"));
        summary.Children.Add(Row("M3 6H20V20H3Z M3 6V3H17 M15 11H22V16H15Z", "Available in intermediate account", "£100.00"));
        summary.Children.Add(Row("M8 10A3 3 0 1 0 8 4A3 3 0 1 0 8 10 M2 21V18Q2 14 8 14Q11 14 13 16 M15 9H22 M19 6L22 9L19 12", "Personal primary transfer", "£200.00"));
        summary.Children.Add(Row("M3 7C3 3 15 3 15 7C15 11 3 11 3 7Z M3 7V13C3 17 15 17 15 13V7 M3 13V19Q7 23 12 20 M16 17H23 M20 14L23 17L20 20", "Personal savings transfer", "£200.00"));
        summary.Children.Add(new Separator());
        summary.Children.Add(new TextBlock
        {
            Text = "Preview only. These are fictional display values, not calculated transfers.",
            TextWrapping = TextWrapping.Wrap
        });
        summary.Children.Add(new Separator());
        summary.Children.Add(new TextBlock
        {
            Text = "Encrypted persistence round trip",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold
        });
        summary.Children.Add(new TextBlock
        {
            Text = "Enter a temporary prototype password. It remains in memory for this run and is not written to settings or logs. Keep it securely: losing it means losing access, and there is no recovery service.",
            TextWrapping = TextWrapping.Wrap
        });
        TextBox password = new() { PlaceholderText = "Prototype password", PasswordChar = '●', MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Stretch };
        summary.Children.Add(password);
        TextBlock persistenceStatus = new() { Text = "Not run", TextWrapping = TextWrapping.Wrap };
        Button runPersistence = new() { Content = "Import, save, reopen and restore synthetic fixture", HorizontalAlignment = HorizontalAlignment.Left };
        runPersistence.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(password.Text))
            {
                persistenceStatus.Text = "Password required. No files changed.";
                return;
            }

            runPersistence.IsEnabled = false;
            try
            {
                string workspace = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dot-finance", "experiment-02");
                string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "synthetic-emma.csv");
                persistenceStatus.Text = PersistenceScenario.RunLocalRoundTrip(workspace, fixture, password.Text);
            }
            catch (Exception exception)
            {
                persistenceStatus.Text = $"Round trip failed safely: {exception.Message}";
            }
            finally
            {
                password.Text = string.Empty;
                runPersistence.IsEnabled = true;
            }
        };
        summary.Children.Add(runPersistence);
        summary.Children.Add(persistenceStatus);
        Content = new ScrollViewer { Content = summary };
    }

    private static Viewbox CreateIcon(string geometry, double size = 24)
    {
        Canvas canvas = new() { Width = 26, Height = 26 };
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse(geometry),
            Stroke = new SolidColorBrush(Color.Parse("#369C94")),
            StrokeThickness = 1.8
        });
        return new Viewbox
        {
            Width = size,
            Height = size,
            Child = canvas,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static Grid Row(string icon, string label, string amount)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("40,*,Auto") };
        row.Children.Add(CreateIcon(icon));
        TextBlock description = new() { Text = label, FontSize = 18, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(description, 1);
        row.Children.Add(description);
        TextBlock value = new() { Text = amount, FontSize = 22, FontWeight = FontWeight.SemiBold };
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        return row;
    }
}
