using System.Text.Json;

namespace DesktopShell;

/// <summary>Persists the non-secret theme preference in a small local settings file.</summary>
public static class ThemeSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "dot-finance",
        "theme.json");

    public static string Load() => Load(DefaultPath);

    public static string Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string value = JsonSerializer.Deserialize<ThemeDocument>(File.ReadAllText(path), JsonOptions)?.Theme ?? "System";
                return value is "System" or "Light" or "Dark" ? value : "System";
            }
        }
        catch (JsonException)
        {
            // A malformed settings file falls back to the system default.
        }

        return "System";
    }

    public static void Save(string theme) => Save(DefaultPath, theme);

    public static void Save(string path, string theme)
    {
        if (theme is not ("System" or "Light" or "Dark"))
        {
            throw new ArgumentOutOfRangeException(nameof(theme), "Theme must be System, Light, or Dark.");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new ThemeDocument(theme), JsonOptions));
    }

    private sealed record ThemeDocument(string Theme);
}