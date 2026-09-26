using System.Runtime.InteropServices;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace DesktopShell;

internal static class LaunchEvidence
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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
                RenderPng(window, Path.Combine(outputDirectory, "window.png"));
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

    public static void CaptureWorkflow(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop, string outputDirectory)
    {
        window.Opened += (_, _) => DispatcherTimer.RunOnce(() =>
        {
            try
            {
                Directory.CreateDirectory(outputDirectory);
                window.PrepareWorkflowCapture();

                window.SetThemeForCapture("Light");
                RenderPng(window, Path.Combine(outputDirectory, "review-before-light.png"));

                window.SetThemeForCapture("Dark");
                RenderPng(window, Path.Combine(outputDirectory, "review-before-dark.png"));

                window.Width = 1040;
                window.Height = 660;
                Dispatcher.UIThread.RunJobs();
                RenderPng(window, Path.Combine(outputDirectory, "review-compact-dark.png"));
                object compactLayout = window.ReviewLayoutEvidence();

                window.Width = 1320;
                window.Height = 820;
                Dispatcher.UIThread.RunJobs();

                object before = WorkflowSnapshot(window.Session);
                window.ApplyWorkflowCaptureCorrection();

                window.SetThemeForCapture("Light");
                RenderPng(window, Path.Combine(outputDirectory, "review-after-light.png"));

                window.SetThemeForCapture("Dark");
                RenderPng(window, Path.Combine(outputDirectory, "review-after-dark.png"));

                File.WriteAllText(
                    Path.Combine(outputDirectory, "workflow.json"),
                    JsonSerializer.Serialize(WorkflowEvidence(window, before, compactLayout), JsonOptions));
                desktop.Shutdown(0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                desktop.Shutdown(1);
            }
        }, TimeSpan.FromSeconds(2));
    }

    private static void RenderPng(MainWindow window, string path)
    {
        Dispatcher.UIThread.RunJobs();
        using RenderTargetBitmap image = new(
            new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
        image.Render(window);
        image.Save(path, PngBitmapEncoderOptions.Default);
    }

    private static object WorkflowEvidence(MainWindow window, object before, object compactLayout)
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        WorkflowSession bill = new();
        bill.Import(Path.Combine(fixtureDirectory, "synthetic-reconciliation.csv"));
        bill.Import(Path.Combine(fixtureDirectory, "synthetic-reconciliation-bill.csv"));

        WorkflowSession refund = new();
        refund.Import(Path.Combine(fixtureDirectory, "synthetic-refund.csv"));

        WorkflowSession shortfall = new();
        shortfall.Import(Path.Combine(fixtureDirectory, "synthetic-reconciliation.csv"));
        shortfall.SetBalance("Joint", 400m);

        WorkflowSession duplicate = new();
        duplicate.Import(Path.Combine(fixtureDirectory, "synthetic-reconciliation.csv"));
        (int Added, int Skipped) = duplicate.Import(Path.Combine(fixtureDirectory, "synthetic-reconciliation.csv"));

        return new
        {
            os = RuntimeInformation.OSDescription,
            framework = RuntimeInformation.FrameworkDescription,
            timestampUtc = DateTimeOffset.UtcNow,
            beforeAcceptedCorrection = before,
            afterAcceptedCorrection = WorkflowSnapshot(window.Session),
            jointBill = WorkflowSnapshot(bill),
            refund = WorkflowSnapshot(refund),
            insufficientFunds = WorkflowSnapshot(shortfall),
            duplicateImport = new { Added, Skipped, transactionCount = duplicate.Allocated.Count },
            compactLayout,
            expected = new
            {
                beforeAcceptedCorrection = new { joint = 450m, sharedSavings = 250m, personal = 400m, requiresReview = 1 },
                afterAcceptedCorrection = new { joint = 475m, sharedSavings = 250m, personal = 375m, requiresReview = 0 },
                jointBill = new { joint = 510m, sharedSavings = 250m, personal = 340m, primary = 200m, personalSavings = 140m },
                refund = new { joint = -50m, direction = "outgoing reimbursement" },
                insufficientFunds = new { account = "Joint", shortfall = 50m },
                duplicateImport = new { added = 0, skipped = 6 },
            },
            scope = "Synthetic guided-reconciliation workflow evidence; not production acceptance."
        };
    }

    private static object WorkflowSnapshot(WorkflowSession session) => new
    {
        cardPaymentTarget = session.Plan.CardPaymentTarget,
        jointContribution = Contribution(session.Plan, FundingSource.Joint),
        sharedSavingsContribution = Contribution(session.Plan, FundingSource.SharedSavings),
        additionalPersonalTransfers = session.Plan.AdditionalPersonalTransfers,
        requiresReview = session.Allocated.Count(transaction => transaction.RequiresReview),
        transfers = session.Plan.Transfers.Select(transfer => new
        {
            source = transfer.Source,
            kind = transfer.Kind.ToString(),
            amount = transfer.Amount,
            transfer.Acknowledged,
        }),
        projectedBalances = session.Plan.ProjectedBalances.Select(balance => new { account = balance.AccountName, balance = balance.ProjectedBalance }),
        shortfalls = session.Plan.Shortfalls.Select(shortfall => new { account = shortfall.AccountName, amount = shortfall.Amount }),
        reportingSubtotals = session.Plan.ReportingSubtotals.Select(subtotal => new { name = subtotal.Name, source = subtotal.FundingSource.ToString(), amount = subtotal.Amount }),
        discrepancy = session.Draft?.Discrepancy,
        discrepancyAcknowledged = session.Draft?.DiscrepancyAcknowledged,
    };

    private static decimal Contribution(TransferPlan plan, FundingSource source) =>
        plan.Contributions.Single(contribution => contribution.Source == source).Total;
}
