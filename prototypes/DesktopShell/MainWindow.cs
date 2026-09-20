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
        summary.Children.Add(new TextBlock { Text = "DOT-FINANCE / EXPERIMENT 01", FontSize = 14 });
        summary.Children.Add(new TextBlock { Text = "Reconciliation overview", FontSize = 32, FontWeight = FontWeight.SemiBold });
        summary.Children.Add(new TextBlock
        {
            Text = "Synthetic demonstration · No financial data loaded",
            FontSize = 16
        });
        summary.Children.Add(new Separator());
        summary.Children.Add(Row("Card payment target", "£1,200.00"));
        summary.Children.Add(Row("Joint contribution", "£450.00"));
        summary.Children.Add(Row("Shared Savings contribution", "£250.00"));
        summary.Children.Add(Row("Available in intermediate account", "£100.00"));
        summary.Children.Add(Row("Personal primary transfer", "£200.00"));
        summary.Children.Add(Row("Personal savings transfer", "£200.00"));
        summary.Children.Add(new Separator());
        summary.Children.Add(new TextBlock
        {
            Text = "Preview only. These are fictional display values, not calculated transfers.\nImports, payments, account settings and storage are not implemented.",
            TextWrapping = TextWrapping.Wrap
        });
        Content = new ScrollViewer { Content = summary };
    }

    private static Grid Row(string label, string amount)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(new TextBlock { Text = label, FontSize = 18, VerticalAlignment = VerticalAlignment.Center });
        TextBlock value = new() { Text = amount, FontSize = 22, FontWeight = FontWeight.SemiBold };
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        return row;
    }
}