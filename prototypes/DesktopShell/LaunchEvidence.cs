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
        window.Opened += (_, _) => DispatcherTimer.RunOnce(() => _ = CaptureWorkflowAsync(window, desktop, outputDirectory), TimeSpan.FromSeconds(2));
    }

    private static async Task CaptureWorkflowAsync(
        MainWindow window,
        IClassicDesktopStyleApplicationLifetime desktop,
        string outputDirectory)
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
            await WaitForCompactWidthAsync(window, 1040);

            window.SetThemeForCapture("Light");
            RenderPng(window, Path.Combine(outputDirectory, "review-compact-light.png"));

            window.SetThemeForCapture("Dark");
            RenderPng(window, Path.Combine(outputDirectory, "review-compact-dark.png"));
            object compactLayout = window.ReviewLayoutEvidence();

            window.Width = 1320;
            window.Height = 820;
            await Task.Delay(250);

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
    }

    private static async Task WaitForCompactWidthAsync(MainWindow window, double width)
    {
        DateTimeOffset timeout = DateTimeOffset.UtcNow.AddSeconds(2);
        while (window.Bounds.Width > width + 0.5 && DateTimeOffset.UtcNow < timeout)
        {
            await Task.Delay(50);
        }

        if (window.Bounds.Width > width + 0.5)
        {
            throw new InvalidOperationException(
                $"Native window did not enter the compact width of {width}; actual width is {window.Bounds.Width}.");
        }
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
        (int added, int skipped) = duplicate.Import(Path.Combine(fixtureDirectory, "synthetic-reconciliation.csv"));

        WorkflowSession reviewBlock = new();
        reviewBlock.Import(Path.Combine(fixtureDirectory, "synthetic-reconciliation.csv"));
        reviewBlock.AcknowledgeDiscrepancy();
        bool unresolvedReviewBlocked;
        try
        {
            reviewBlock.AcknowledgeTransfer("Joint", TransferKind.Funding);
            unresolvedReviewBlocked = false;
        }
        catch (InvalidOperationException)
        {
            unresolvedReviewBlocked = true;
        }

        WorkflowSession nonGbp = new();
        nonGbp.Import(Path.Combine(fixtureDirectory, "synthetic-non-gbp.csv"));
        bool nonGbpInitiallyRequiresReview = nonGbp.Allocated.Single().RequiresReview;
        nonGbp.SetActualGbpAmount("R-0200", -275m);

        WorkflowSession correction = new();
        correction.Import(Path.Combine(fixtureDirectory, "synthetic-reconciliation.csv"));
        correction.SetOverride("R-0005", FundingSource.Joint);
        correction.AcknowledgeDiscrepancy();
        correction.AcknowledgeTransfer("Joint", TransferKind.Funding);
        correction.Import(Path.Combine(fixtureDirectory, "synthetic-reconciliation-changed.csv"));
        int queuedCorrections = correction.PendingCorrections.Count;
        AcknowledgeOutstandingFunding(correction);
        correction.AcknowledgeCardPaid();
        correction.AcceptPendingCorrection("R-0001");
        ImportResult acceptedCorrectionReplay = correction.Import(Path.Combine(fixtureDirectory, "synthetic-reconciliation-changed.csv"));

        WorkflowSession reopenGuard = new();
        reopenGuard.Import(Path.Combine(fixtureDirectory, "synthetic-reconciliation.csv"));
        reopenGuard.SetOverride("R-0005", FundingSource.Joint);
        reopenGuard.AcknowledgeDiscrepancy();
        AcknowledgeOutstandingFunding(reopenGuard);
        reopenGuard.AcknowledgeCardPaid();
        reopenGuard.SetCardPaymentTarget(900m);
        bool changedTargetBlocksReopen;
        try
        {
            reopenGuard.ReopenLatestCompleted();
            changedTargetBlocksReopen = false;
        }
        catch (InvalidOperationException)
        {
            changedTargetBlocksReopen = true;
        }

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
            duplicateImport = new { added, skipped, transactionCount = duplicate.Allocated.Count },
            reviewBlock = new { blocked = unresolvedReviewBlocked, locked = reviewBlock.Draft?.IsLocked },
            nonGbp = new
            {
                initiallyRequiresReview = nonGbpInitiallyRequiresReview,
                requiresReviewAfterGbpEntry = nonGbp.Allocated.Single().RequiresReview,
                actualGbpAmount = nonGbp.Allocated.Single().ActualGbpAmount,
                jointContribution = Contribution(nonGbp.Plan, FundingSource.Joint),
            },
            correction = new
            {
                queued = queuedCorrections,
                remaining = correction.PendingCorrections.Count,
                adjustmentTransactions = correction.Allocated.Count,
                jointAdjustment = Contribution(correction.Plan, FundingSource.Joint),
                preservedOriginalAmount = correction.CompletedReconciliations.Single().Transactions
                    .Single(transaction => transaction.Id == "R-0001").Amount,
                acceptedReplaySkipped = acceptedCorrectionReplay.Skipped,
            },
            reopenGuard = new { changedTargetBlocksReopen },
            compactLayout,
            expected = new
            {
                beforeAcceptedCorrection = new { joint = 450m, sharedSavings = 250m, personal = 400m, requiresReview = 1 },
                afterAcceptedCorrection = new { joint = 475m, sharedSavings = 250m, personal = 375m, requiresReview = 0 },
                jointBill = new { joint = 510m, sharedSavings = 250m, personal = 340m, primary = 200m, personalSavings = 140m },
                refund = new { joint = -50m, direction = "outgoing reimbursement" },
                insufficientFunds = new { account = "Joint", shortfall = 50m },
                duplicateImport = new { added = 0, skipped = 6 },
                reviewBlock = new { blocked = true, locked = false },
                nonGbp = new { actualGbpAmount = -275m, jointContribution = 275m },
                correction = new { queued = 1, remaining = 0, adjustmentTransactions = 2, jointAdjustment = 20m, preservedOriginalAmount = -300m, acceptedReplaySkipped = 1 },
                reopenGuard = new { changedTargetBlocksReopen = true },
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

    private static void AcknowledgeOutstandingFunding(WorkflowSession session)
    {
        foreach (Transfer transfer in session.Draft!.Transfers
            .Where(transfer => transfer.Kind != TransferKind.CardPayment && !transfer.Acknowledged))
        {
            session.AcknowledgeTransfer(transfer.Source, transfer.Kind);
        }
    }
}