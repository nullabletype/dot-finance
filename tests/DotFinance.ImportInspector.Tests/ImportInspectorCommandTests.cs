using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

using DocumentFormat.OpenXml.Spreadsheet;

using DotFinance.ImportInspector;

using Xunit;

namespace DotFinance.ImportInspector.Tests;

[CollectionDefinition("Process umask", DisableParallelization = true)]
public sealed class ProcessUmaskCollection
{
}

[Collection("Process umask")]
public sealed class ImportInspectorCommandTests
{
    [Theory]
    [InlineData("\"", "\"")]
    [InlineData("'", "'")]
    [InlineData("\u00A0", "\u00A0")]
    public void Run_PastedPaths_NormalisesTerminalDecoration(string prefix, string suffix)
    {
        using TemporaryDirectory temporary = new();
        string privateDirectory = Path.Combine(temporary.Path, "private files");
        Directory.CreateDirectory(privateDirectory);
        string input = Path.Combine(privateDirectory, "private export.xlsx");
        string outputDirectory = Path.Combine(temporary.Path, "shareable files");
        File.WriteAllBytes(input, TestWorkbookFactory.Create());
        StringWriter standardOutput = new();
        StringWriter standardError = new();

        int exitCode = ImportInspectorCommand.Run(
            ["inspect"],
            standardOutput,
            standardError,
            PathReader(prefix + input + suffix, prefix + outputDirectory + suffix));

        Assert.Equal(0, exitCode);
        Assert.Empty(standardError.ToString());
        Assert.True(File.Exists(Path.Combine(outputDirectory, ImportInspectorCommand.ReportFileName)));
    }

    [Fact]
    public void Run_PosixShellEscapedPastedPaths_NormalisesEscapes()
    {
        if (OperatingSystem.IsWindows()) return;

        using TemporaryDirectory temporary = new();
        string privateDirectory = Path.Combine(temporary.Path, "private files");
        Directory.CreateDirectory(privateDirectory);
        string input = Path.Combine(privateDirectory, "private export.xlsx");
        string outputDirectory = Path.Combine(temporary.Path, "shareable files");
        File.WriteAllBytes(input, TestWorkbookFactory.Create());
        StringWriter standardOutput = new();
        StringWriter standardError = new();

        int exitCode = ImportInspectorCommand.Run(
            ["inspect"],
            standardOutput,
            standardError,
            PathReader(input.Replace(" ", "\\ ", StringComparison.Ordinal), outputDirectory.Replace(" ", "\\ ", StringComparison.Ordinal)));

        Assert.Equal(0, exitCode);
        Assert.Empty(standardError.ToString());
        Assert.True(File.Exists(Path.Combine(outputDirectory, ImportInspectorCommand.ReportFileName)));
    }

    [Fact]
    public void Run_ValidExport_WritesOnlyApprovedFilesAndSafeConsoleOutput()
    {
        using TemporaryDirectory temporary = new();
        string privateDirectory = Path.Combine(temporary.Path, "private");
        Directory.CreateDirectory(privateDirectory);
        string input = Path.Combine(privateDirectory, "private-export.xlsx");
        string outputDirectory = Path.Combine(temporary.Path, "shareable");
        File.WriteAllBytes(input, TestWorkbookFactory.Create(rows: [TestWorkbookFactory.RepresentativeRow("private-id")]));
        StringWriter standardOutput = new();
        StringWriter standardError = new();

        int exitCode = ImportInspectorCommand.Run(["inspect"], standardOutput, standardError, PathReader(input, outputDirectory));

        Assert.Equal(0, exitCode);
        Assert.Empty(standardError.ToString());
        Assert.DoesNotContain(input, standardOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(outputDirectory, standardOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-id", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal([ImportInspectorCommand.ReportFileName, ImportInspectorCommand.SyntheticFileName],
            Directory.GetFiles(outputDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        string report = File.ReadAllText(Path.Combine(outputDirectory, ImportInspectorCommand.ReportFileName));
        byte[] synthetic = File.ReadAllBytes(Path.Combine(outputDirectory, ImportInspectorCommand.SyntheticFileName));
        Assert.DoesNotContain("private-id", report, StringComparison.Ordinal);
        Assert.DoesNotContain("private-id", TestWorkbookFactory.ReadAllText(synthetic), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_ValidExport_SetsOwnerOnlyPermissionsOnBothReports()
    {
        using TemporaryDirectory temporary = new();
        string privateDirectory = Path.Combine(temporary.Path, "private");
        Directory.CreateDirectory(privateDirectory);
        string input = Path.Combine(privateDirectory, "private-export.xlsx");
        string outputDirectory = Path.Combine(temporary.Path, "shareable");
        File.WriteAllBytes(input, TestWorkbookFactory.Create());
        StringWriter output = new();
        StringWriter error = new();

        int exitCode = ImportInspectorCommand.Run(["inspect"], output, error, PathReader(input, outputDirectory));

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        foreach (string fileName in new[] { ImportInspectorCommand.ReportFileName, ImportInspectorCommand.SyntheticFileName })
        {
            string path = Path.Combine(outputDirectory, fileName);
            Assert.True(File.Exists(path));
            if (OperatingSystem.IsWindows())
            {
                AssertCurrentUserOnlyAcl(path);
            }
            else
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }
    }

    [Fact]
    public void WriteRestrictedFile_RestrictiveUmask_NormalisesUnixPermissions()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "report.json");
        byte[] content = "synthetic report"u8.ToArray();

        const uint allPermissionBits = 0x1FF; // 0777
        uint previousMask = SetProcessUmask(allPermissionBits);
        try
        {
            ImportInspectorCommand.WriteRestrictedFile(path, content);
        }
        finally
        {
            _ = SetProcessUmask(previousMask);
        }

        Assert.Equal(content, File.ReadAllBytes(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

#pragma warning disable SYSLIB1054 // Test-only primitive binding avoids enabling unsafe code for the project.
    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint SetProcessUmask(uint mask);
#pragma warning restore SYSLIB1054

    [SupportedOSPlatform("windows")]
    private static void AssertCurrentUserOnlyAcl(string path)
    {
        FileSecurity security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected, "The report ACL must not inherit from its parent directory.");
        List<FileSystemAccessRule> rules = [.. security
            .GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()];
        IdentityReference currentUser = WindowsIdentity.GetCurrent().User!;
        FileSystemAccessRule rule = Assert.Single(rules);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(currentUser, rule.IdentityReference);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
    }

    [Fact]
    public void Run_ValidExport_StatesProhibitedDestinationsAndPrivateHandoff()
    {
        using TemporaryDirectory temporary = new();
        string privateDirectory = Path.Combine(temporary.Path, "private");
        Directory.CreateDirectory(privateDirectory);
        string input = Path.Combine(privateDirectory, "private-export.xlsx");
        string outputDirectory = Path.Combine(temporary.Path, "shareable");
        File.WriteAllBytes(input, TestWorkbookFactory.Create());
        StringWriter output = new();
        StringWriter error = new();

        int exitCode = ImportInspectorCommand.Run(["inspect"], output, error, PathReader(input, outputDirectory));

        Assert.Equal(0, exitCode);
        string console = output.ToString();
        Assert.Contains("full inspection report", console, StringComparison.Ordinal);
        Assert.Contains("deliberately minimised report", console, StringComparison.Ordinal);
        Assert.Contains("private channel", console, StringComparison.Ordinal);
        Assert.Contains("committed", console, StringComparison.Ordinal);
        Assert.Contains("uploaded to GitHub", console, StringComparison.Ordinal);
        Assert.Contains("agent context", console, StringComparison.Ordinal);
        Assert.Contains("CI logs", console, StringComparison.Ordinal);
        Assert.Contains("otherwise published", console, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_WriteFailure_StillStatesProhibitedDestinationsAndPrivateHandoff()
    {
        using TemporaryDirectory temporary = new();
        string privateDirectory = Path.Combine(temporary.Path, "private");
        Directory.CreateDirectory(privateDirectory);
        string input = Path.Combine(privateDirectory, "private-export.xlsx");
        string blocker = Path.Combine(temporary.Path, "blocker");
        File.WriteAllText(blocker, "file");
        string outputDirectory = Path.Combine(blocker, "shareable");
        File.WriteAllBytes(input, TestWorkbookFactory.Create());
        StringWriter output = new();
        StringWriter error = new();

        int exitCode = ImportInspectorCommand.Run(["inspect"], output, error, PathReader(input, outputDirectory));

        Assert.Equal(73, exitCode);
        string console = error.ToString();
        Assert.Contains("full inspection report", console, StringComparison.Ordinal);
        Assert.Contains("deliberately minimised report", console, StringComparison.Ordinal);
        Assert.Contains("private channel", console, StringComparison.Ordinal);
        Assert.Contains("committed", console, StringComparison.Ordinal);
        Assert.Contains("uploaded to GitHub", console, StringComparison.Ordinal);
        Assert.Contains("agent context", console, StringComparison.Ordinal);
        Assert.Contains("CI logs", console, StringComparison.Ordinal);
        Assert.Contains("otherwise published", console, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_UnsafePrivateInput_EmitsOnlySafeDiagnosticAndReport()
    {
        using TemporaryDirectory temporary = new();
        string privateDirectory = Path.Combine(temporary.Path, "private");
        Directory.CreateDirectory(privateDirectory);
        string input = Path.Combine(privateDirectory, "private.xlsx");
        string outputDirectory = Path.Combine(temporary.Path, "shareable");
        File.WriteAllBytes(input, TestWorkbookFactory.Create(configure: (_, _, worksheetPart, _) =>
            worksheetPart.Worksheet!.Descendants<Cell>().Last().CellFormula = new CellFormula("\"PRIVATE-SECRET\"")));
        StringWriter output = new();
        StringWriter error = new();

        int exitCode = ImportInspectorCommand.Run(["inspect"], output, error, PathReader(input, outputDirectory));

        Assert.Equal(2, exitCode);
        Assert.Empty(error.ToString());
        Assert.DoesNotContain("PRIVATE-SECRET", output.ToString(), StringComparison.Ordinal);
        string report = File.ReadAllText(Path.Combine(outputDirectory, ImportInspectorCommand.ReportFileName));
        Assert.Contains("FORMULA_PRESENT", report, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-SECRET", report, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(outputDirectory, ImportInspectorCommand.SyntheticFileName)));
    }

    [Fact]
    public void Run_InputInsideOutputOrMissingArguments_FailsWithoutWriting()
    {
        using TemporaryDirectory temporary = new();
        string privateDirectory = Path.Combine(temporary.Path, "private");
        Directory.CreateDirectory(privateDirectory);
        string input = Path.Combine(privateDirectory, "private.xlsx");
        File.WriteAllText(input, "private");
        StringWriter output = new();
        StringWriter error = new();

        int pathExit = ImportInspectorCommand.Run(["inspect"], output, error, PathReader(input, temporary.Path));
        int usageExit = ImportInspectorCommand.Run([], output, error, PathReader(input, temporary.Path));

        Assert.Equal(64, pathExit);
        Assert.Equal(64, usageExit);
        Assert.DoesNotContain(input, error.ToString(), StringComparison.Ordinal);
        Assert.Equal([input], Directory.GetFiles(temporary.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Run_NonEmptyOutputDirectory_FailsWithoutReadingOrOverwriting()
    {
        using TemporaryDirectory temporary = new();
        string privateDirectory = Path.Combine(temporary.Path, "private");
        string outputDirectory = Path.Combine(temporary.Path, "shareable");
        Directory.CreateDirectory(privateDirectory);
        Directory.CreateDirectory(outputDirectory);
        string input = Path.Combine(privateDirectory, "private.xlsx");
        string existing = Path.Combine(outputDirectory, "keep.txt");
        File.WriteAllText(input, "PRIVATE-SECRET");
        File.WriteAllText(existing, "keep");
        StringWriter output = new();
        StringWriter error = new();

        int exitCode = ImportInspectorCommand.Run(["inspect"], output, error, PathReader(input, outputDirectory));

        Assert.Equal(73, exitCode);
        Assert.DoesNotContain(input, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(outputDirectory, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-SECRET", error.ToString(), StringComparison.Ordinal);
        Assert.Equal("keep", File.ReadAllText(existing));
        Assert.Single(Directory.GetFiles(outputDirectory));
    }

    [Fact]
    public void Run_PrivatePathsPassedAsArguments_AreRejectedWithoutReadingOrWriting()
    {
        using TemporaryDirectory temporary = new();
        string input = Path.Combine(temporary.Path, "PRIVATE-EXPORT.xlsx");
        string outputDirectory = Path.Combine(temporary.Path, "shareable");
        File.WriteAllText(input, "PRIVATE-SECRET");
        StringWriter output = new();
        StringWriter error = new();
        bool readAttempted = false;

        int exitCode = ImportInspectorCommand.Run(
            ["inspect", input, outputDirectory],
            output,
            error,
            () =>
            {
                readAttempted = true;
                return null;
            });

        Assert.Equal(64, exitCode);
        Assert.False(readAttempted);
        Assert.Empty(output.ToString());
        Assert.DoesNotContain(input, error.ToString(), StringComparison.Ordinal);
        Assert.Equal([input], Directory.GetFiles(temporary.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Run_HiddenPathReaderFailure_EmitsOnlyFixedDiagnostic()
    {
        StringWriter output = new();
        StringWriter error = new();

        int exitCode = ImportInspectorCommand.Run(
            ["inspect"], output, error, () => throw new InvalidOperationException("PRIVATE-PATH-AND-TERMINAL-DETAILS"));

        Assert.Equal(64, exitCode);
        Assert.Equal("Interactive hidden path entry failed." + Environment.NewLine, error.ToString());
        Assert.DoesNotContain("PRIVATE", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_SymbolicLinkPath_IsRejectedBeforePrivateInputIsRead()
    {
        using TemporaryDirectory temporary = new();
        string privateDirectory = Path.Combine(temporary.Path, "private");
        Directory.CreateDirectory(privateDirectory);
        string input = Path.Combine(privateDirectory, "private.xlsx");
        File.WriteAllText(input, "PRIVATE-SECRET");
        string outputLink = Path.Combine(temporary.Path, "shareable-link");
        try
        {
            Directory.CreateSymbolicLink(outputLink, privateDirectory);
        }
        catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException)
        {
            Assert.True(OperatingSystem.IsWindows());
            return;
        }

        StringWriter output = new();
        StringWriter error = new();

        int exitCode = ImportInspectorCommand.Run(["inspect"], output, error, PathReader(input, outputLink));

        Assert.Equal(64, exitCode);
        Assert.Contains("Symbolic links are not supported", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-SECRET", output.ToString(), StringComparison.Ordinal);
        Assert.Equal([input], Directory.GetFiles(privateDirectory));
    }

    [Fact]
    public void Run_SymbolicLinkAncestor_IsResolvedBeforeSeparationCheck()
    {
        using TemporaryDirectory temporary = new();
        string privateDirectory = Path.Combine(temporary.Path, "private");
        Directory.CreateDirectory(privateDirectory);
        string input = Path.Combine(privateDirectory, "private.xlsx");
        File.WriteAllText(input, "PRIVATE-SECRET");
        string linkedParent = Path.Combine(temporary.Path, "linked-parent");
        try
        {
            Directory.CreateSymbolicLink(linkedParent, privateDirectory);
        }
        catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException)
        {
            Assert.True(OperatingSystem.IsWindows());
            return;
        }

        StringWriter output = new();
        StringWriter error = new();
        string outputThroughLink = Path.Combine(linkedParent, "shareable");

        int exitCode = ImportInspectorCommand.Run(["inspect"], output, error, PathReader(input, outputThroughLink));

        Assert.Equal(64, exitCode);
        Assert.Contains("must be separate", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-SECRET", output.ToString(), StringComparison.Ordinal);
        Assert.Equal([input], Directory.GetFiles(privateDirectory));
    }

    private static Func<string?> PathReader(params string[] paths)
    {
        Queue<string> remaining = new(paths);
        return () => remaining.Count == 0 ? null : remaining.Dequeue();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dot-finance-inspector-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}