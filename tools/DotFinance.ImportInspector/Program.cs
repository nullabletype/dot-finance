using System.Text;

using DotFinance.ImportInspector;

return ImportInspectorCommand.Run(args, Console.Out, Console.Error, ReadHiddenLine);

static string? ReadHiddenLine()
{
    if (Console.IsInputRedirected) return null;

    StringBuilder value = new();
    while (true)
    {
        ConsoleKeyInfo key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) return value.ToString();
        if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)) return null;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (value.Length > 0) value.Length--;
            continue;
        }

        if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
    }
}