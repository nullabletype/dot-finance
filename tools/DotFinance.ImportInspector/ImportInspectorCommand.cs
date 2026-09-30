using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace DotFinance.ImportInspector;

public static class ImportInspectorCommand
{
    public const string ReportFileName = "shareable-report.json";
    public const string SyntheticFileName = "synthetic-export.xlsx";

    internal const string SharingNotice =
        "Keep the full inspection report (shareable-report.json) local and share only the deliberately minimised report " +
        "(synthetic-export.xlsx) through an explicitly private channel after reviewing it. Neither report may be committed, " +
        "uploaded to GitHub, placed in agent context, copied into CI logs, or otherwise published.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static int Run(IReadOnlyList<string> arguments, TextWriter output, TextWriter error, Func<string?>? readHiddenPath = null)
    {
        if (arguments.Count != 1 || !string.Equals(arguments[0], "inspect", StringComparison.Ordinal))
        {
            error.WriteLine("Usage: dot-finance-import-inspector inspect");
            return 64;
        }

        if (readHiddenPath is null)
        {
            error.WriteLine("Interactive hidden path entry is unavailable.");
            return 64;
        }

        string? inputPathText;
        string? outputDirectoryText;
        try
        {
            output.Write("Private export path (input hidden): ");
            inputPathText = readHiddenPath();
            output.WriteLine();
            output.Write("New empty output directory (input hidden): ");
            outputDirectoryText = readHiddenPath();
            output.WriteLine();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            error.WriteLine("Interactive hidden path entry failed.");
            return 64;
        }

        if (string.IsNullOrWhiteSpace(inputPathText) || string.IsNullOrWhiteSpace(outputDirectoryText))
        {
            error.WriteLine("Both paths are required.");
            return 64;
        }

        string inputPath;
        string outputDirectory;
        try
        {
            string lexicalInputPath = Path.GetFullPath(NormalizeInteractivePath(inputPathText));
            string lexicalOutputDirectory = Path.GetFullPath(NormalizeInteractivePath(outputDirectoryText));
            if (IsDirectSymbolicLink(lexicalInputPath) || IsDirectSymbolicLink(lexicalOutputDirectory))
            {
                error.WriteLine("Symbolic links are not supported for private inspection paths.");
                return 64;
            }

            inputPath = ResolvePhysicalPath(lexicalInputPath);
            outputDirectory = ResolvePhysicalPath(lexicalOutputDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine("The input or output path is invalid.");
            return 64;
        }

        if (!File.Exists(inputPath))
        {
            error.WriteLine("The private export could not be opened.");
            return 66;
        }

        string inputDirectory = Path.GetDirectoryName(inputPath)!;
        if (IsWithin(inputPath, outputDirectory) || IsWithin(outputDirectory, inputDirectory))
        {
            error.WriteLine("The output directory must be separate from the private export directory.");
            return 64;
        }

        try
        {
            if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            {
                error.WriteLine("The output directory must be new or empty.");
                return 73;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine("The output directory could not be inspected.");
            return 73;
        }

        byte[] bytes;
        try
        {
            FileInfo source = new(inputPath);
            if (source.Length > ExportInspector.MaximumInputBytes)
            {
                error.WriteLine("The private export exceeds the 32 MiB inspection limit.");
                return 65;
            }

            bytes = File.ReadAllBytes(inputPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine("The private export could not be read.");
            return 66;
        }

        byte[]? syntheticBytes = null;
        try
        {
            InspectionResult result = ExportInspector.Inspect(bytes);
            syntheticBytes = result.SyntheticWorkbook;
            Directory.CreateDirectory(outputDirectory);
            if (!PathEquals(outputDirectory, ResolvePhysicalPath(outputDirectory)))
                throw new IOException("The output path changed during inspection.");
            WriteAtomically(Path.Combine(outputDirectory, ReportFileName), JsonSerializer.Serialize(result.Report, JsonOptions) + Environment.NewLine);
            if (syntheticBytes is not null)
                WriteAtomically(Path.Combine(outputDirectory, SyntheticFileName), syntheticBytes);

            if (result.Report.Status == "error")
            {
                output.WriteLine($"Inspection completed with structural errors; no synthetic workbook was generated. {SharingNotice}");
            }
            else
            {
                output.WriteLine($"Inspection completed. {SharingNotice}");
            }
            return result.Report.Status == "error" ? 2 : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine("The sanitised output could not be written.");
            return 73;
        }
        catch (Exception)
        {
            error.WriteLine("The export could not be inspected safely.");
            return 70;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (syntheticBytes is not null) CryptographicOperations.ZeroMemory(syntheticBytes);
        }
    }

    private static void WriteAtomically(string path, string content)
    {
        byte[] encoded = new UTF8Encoding(false).GetBytes(content);
        WriteAtomically(path, encoded);
    }

    private static string NormalizeInteractivePath(string path)
    {
        string normalized = path.Trim();
        bool quoted = normalized.Length >= 2
            && (normalized[0] == '\'' && normalized[^1] == '\''
                || normalized[0] == '"' && normalized[^1] == '"');
        if (quoted) normalized = normalized[1..^1];

        if (!quoted && !OperatingSystem.IsWindows() && normalized.Contains('\\', StringComparison.Ordinal))
        {
            StringBuilder unescaped = new(normalized.Length);
            for (int index = 0; index < normalized.Length; index++)
            {
                if (normalized[index] == '\\' && index + 1 < normalized.Length)
                    index++;
                unescaped.Append(normalized[index]);
            }

            normalized = unescaped.ToString();
        }

        if (normalized == "~" || normalized.StartsWith("~/", StringComparison.Ordinal)
            || OperatingSystem.IsWindows() && normalized.StartsWith("~\\", StringComparison.Ordinal))
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile)) normalized = Path.Combine(userProfile, normalized[1..].TrimStart('/', '\\'));
        }

        return normalized;
    }

    private static void WriteAtomically(string path, byte[] content)
    {
        string directory = Path.GetDirectoryName(path)!;
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Path.GetRandomFileName()}.tmp");
        try
        {
            WriteRestrictedFile(temporary, content);
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WriteRestrictedFile(string path, byte[] content)
    {
        if (OperatingSystem.IsWindows())
        {
            // Create the file empty, restrict it, then write so report content is
            // never present under an inherited access control list.
            FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Dispose();
            ApplyOwnerOnlyAcl(path);
            using FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.Write(content);
            return;
        }

        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        };
        using (FileStream stream = new(path, options))
            stream.Write(content);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyOwnerOnlyAcl(string path)
    {
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user identity is unavailable.");
        FileSecurity security = new();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static string ResolvePhysicalPath(string path)
    {
        string root = Path.GetPathRoot(path)!;
        string resolved = root;
        string relative = Path.GetRelativePath(root, path);
        foreach (string segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(resolved, segment);
            FileSystemInfo current = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
            current.Refresh();
            resolved = current.Exists && File.GetAttributes(candidate).HasFlag(FileAttributes.ReparsePoint)
                ? ResolvePhysicalPath(current.ResolveLinkTarget(returnFinalTarget: true)!.FullName)
                : candidate;
        }

        return Path.GetFullPath(resolved);
    }

    private static bool IsDirectSymbolicLink(string path) => (File.Exists(path) || Directory.Exists(path))
        && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static bool PathEquals(string first, string second) => string.Equals(
        first, second, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsWithin(string path, string directory)
    {
        string relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}