using System.Globalization;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace DesktopShell;

public sealed class MainWindow : Window
{
    private static readonly CultureInfo BritishCulture = CultureInfo.GetCultureInfo("en-GB");
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#FF4FA3"));
    private static readonly IBrush AccentSoftBrush = new SolidColorBrush(Color.Parse("#24FF4FA3"));
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#79E5B0"));
    private static readonly IBrush SuccessSoftBrush = new SolidColorBrush(Color.Parse("#1979E5B0"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#FFC56B"));
    private static readonly IBrush WarningSoftBrush = new SolidColorBrush(Color.Parse("#19FFC56B"));
    private static readonly IBrush VioletBrush = new SolidColorBrush(Color.Parse("#BBA5FF"));
    private static readonly IBrush NeutralSoftBrush = new SolidColorBrush(Color.FromArgb(16, 128, 128, 128));
    private static readonly IBrush NeutralRaisedBrush = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128));
    private static readonly IBrush LineBrush = new SolidColorBrush(Color.FromArgb(55, 128, 128, 128));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.FromArgb(210, 128, 128, 128));
    private static readonly FontFamily MonospaceFont = new("Menlo, Consolas, monospace");

    private readonly TextBlock _importStatus;
    private readonly StackPanel _transactionList;
    private readonly StackPanel _sidebar;
    private readonly StackPanel _transfersPanel;
    private readonly StackPanel _ackPanel;
    private readonly StackPanel _historyPanel;
    private readonly TextBlock _previewText;
    private readonly TextBlock _persistenceStatus;
    private readonly TextBlock _breadcrumb;
    private readonly TextBlock _netTotal;
    private readonly TextBlock _filterCount;
    private readonly TextBlock _reviewProgress;
    private readonly ContentControl _workspace;
    private readonly ContentControl _transactionDetails;
    private readonly Dictionary<string, TextBox> _balanceBoxes = new(StringComparer.Ordinal);
    private readonly List<Button> _stageButtons = [];
    private readonly List<Control> _pages = [];
    private readonly TextBox _targetBox;
    private readonly ComboBox _themeBox;
    private readonly TextBox _searchBox;
    private readonly ComboBox _accountFilter;
    private readonly ComboBox _categoryFilter;
    private readonly ComboBox _statusFilter;
    private readonly ComboBox _overrideIdBox;
    private readonly TextBlock _sessionReviewCount;
    private int _selectedStage;
    private string? _selectedTransactionId;
    private bool _suppressThemeSave;
    private bool _updatingFilters;
    private Border _ledgerFrame = null!;
    private Border _reviewInspector = null!;
    private ScrollViewer _ledgerScroll = null!;
    private Border _filterFrame = null!;
    private StackPanel _filterContent = null!;
    private ScrollViewer _filterScroll = null!;

    public WorkflowSession Session { get; } = new();

    public MainWindow()
    {
        Title = "dot-finance · Guided reconciliation prototype";
        Width = 1320;
        Height = 820;
        MinWidth = 1040;
        MinHeight = 660;

        _importStatus = MutedText("No fixture imported yet.");
        _transactionList = new StackPanel();
        _sidebar = new StackPanel { Spacing = 2 };
        _transfersPanel = new StackPanel { Spacing = 8 };
        _ackPanel = new StackPanel { Spacing = 8 };
        _historyPanel = new StackPanel { Spacing = 8 };
        _previewText = MutedText("No preview.");
        _persistenceStatus = MutedText("Not saved this session.");
        _breadcrumb = MutedText("September reconciliation  /  Import");
        _netTotal = new TextBlock
        {
            Text = "£0.00",
            FontFamily = MonospaceFont,
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _filterCount = MutedText("0 of 0");
        _filterCount.VerticalAlignment = VerticalAlignment.Center;
        _reviewProgress = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold };
        _workspace = new ContentControl();
        _transactionDetails = new ContentControl();
        _sessionReviewCount = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold };

        foreach (Account account in Session.Accounts)
        {
            _balanceBoxes[account.Name] = CompactTextBox(account.CurrentBalance.ToString("0.00", CultureInfo.InvariantCulture), 112);
        }

        _targetBox = CompactTextBox(Session.CardPaymentTarget.ToString("0.00", CultureInfo.InvariantCulture), 112);
        _themeBox = CompactComboBox(["System", "Light", "Dark"], 104);
        _themeBox.SelectedItem = ThemeSettings.Load();

        _searchBox = CompactTextBox(string.Empty, 205);
        _searchBox.PlaceholderText = "Search merchant, note or ID";
        _accountFilter = CompactComboBox(["All accounts"], 135);
        _accountFilter.SelectedIndex = 0;
        _categoryFilter = CompactComboBox(["All categories"], 135);
        _categoryFilter.SelectedIndex = 0;
        _statusFilter = CompactComboBox(["All statuses", "Ready", "Review", "Excluded"], 124);
        _statusFilter.SelectedIndex = 0;

        _overrideIdBox = CompactComboBox([], 126);
        _overrideIdBox.PlaceholderText = "Transaction ID";

        Content = BuildLayout();
        WireFilters();
        Refresh();
        SelectStage(0);
    }

    private Grid BuildLayout()
    {
        Grid root = new()
        {
            RowDefinitions = new RowDefinitions("64,*"),
            ColumnDefinitions = new ColumnDefinitions("226,*"),
        };
        Border header = BuildHeader();
        Grid.SetColumnSpan(header, 2);
        root.Children.Add(header);
        Border navigation = BuildStageNavigation();
        Grid.SetRow(navigation, 1);
        root.Children.Add(navigation);
        _pages.Add(BuildImportPage());
        _pages.Add(BuildReviewPage());
        _pages.Add(BuildTransfersPage());
        _pages.Add(BuildAcknowledgePage());
        _pages.Add(BuildHistoryPage());
        Grid.SetRow(_workspace, 1);
        Grid.SetColumn(_workspace, 1);
        root.Children.Add(_workspace);
        return root;
    }

    private Border BuildHeader()
    {
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("226,*,Auto"), Margin = new Thickness(18, 0) };
        StackPanel brand = new() { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(CreateBrandMark(26));
        brand.Children.Add(new TextBlock
        {
            Text = "dot-finance",
            FontSize = 17,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(brand);
        _breadcrumb.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_breadcrumb, 1);
        header.Children.Add(_breadcrumb);
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        _themeBox.SelectionChanged += (_, _) => ApplyThemeSelection();
        actions.Children.Add(_themeBox);
        Button save = ActionButton("Save draft", "save");
        save.Click += async (_, _) => await SaveScenarioAsync();
        actions.Children.Add(save);
        Button reopen = ActionButton("Reopen", "history");
        reopen.Click += async (_, _) => await LoadScenarioAsync();
        actions.Children.Add(reopen);
        Grid.SetColumn(actions, 2);
        header.Children.Add(actions);
        return new Border
        {
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = NeutralSoftBrush,
            Child = header,
        };
    }

    private Border BuildStageNavigation()
    {
        StackPanel content = new() { Margin = new Thickness(12, 22, 12, 16), Spacing = 5 };
        TextBlock navigationLabel = SectionLabel("RECONCILIATION", new Thickness(10, 0, 0, 10));
        navigationLabel.FontSize = 10;
        content.Children.Add(navigationLabel);
        AddStage(content, "Import", "upload", "4 fixtures");
        AddStage(content, "Review", "review", "transactions");
        AddStage(content, "Transfers", "arrow", "plan");
        AddStage(content, "Confirm", "check", "manual");
        AddStage(content, "History", "history", string.Empty);
        TextBlock sessionLabel = SectionLabel("THIS SESSION");
        sessionLabel.FontSize = 10;
        TextBlock sessionDescription = MutedText("Changes remain local and use synthetic financial data.");
        sessionDescription.FontSize = 12;
        Border sessionCard = Card(new StackPanel
        {
            Spacing = 7,
            Children =
            {
                sessionLabel,
                _sessionReviewCount,
                sessionDescription,
            },
        }, new Thickness(13));
        sessionCard.Margin = new Thickness(2, 24, 2, 0);
        content.Children.Add(sessionCard);
        return new Border
        {
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Background = NeutralSoftBrush,
            Child = content,
        };
    }

    private void AddStage(Panel panel, string label, string icon, string suffix)
    {
        int index = _stageButtons.Count;
        Grid content = new() { ColumnDefinitions = new ColumnDefinitions("30,*,Auto") };
        content.Children.Add(new Border
        {
            Width = 27,
            Height = 27,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = CreateIcon(icon, MutedBrush, 14),
        });
        TextBlock title = new() { Text = label, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 14, FontWeight = FontWeight.SemiBold };
        Grid.SetColumn(title, 1);
        content.Children.Add(title);
        TextBlock count = new() { Text = suffix, Foreground = MutedBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(count, 2);
        content.Children.Add(count);
        Button button = new()
        {
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 10),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(9),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Click += (_, _) => SelectStage(index);
        _stageButtons.Add(button);
        panel.Children.Add(button);
    }

    private ScrollViewer BuildImportPage()
    {
        StackPanel page = PageStack();
        page.Children.Add(PageHeader("STEP 1 OF 4", "Import statements", "Bring fictional exports into this local reconciliation.", null));
        StackPanel fixtureButtons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        fixtureButtons.Children.Add(ImportButton("Base card purchases", "synthetic-reconciliation.csv"));
        fixtureButtons.Children.Add(ImportButton("£60 Joint bill", "synthetic-reconciliation-bill.csv"));
        fixtureButtons.Children.Add(ImportButton("Refund example", "synthetic-refund.csv"));
        fixtureButtons.Children.Add(ImportButton("Non-GBP review", "synthetic-non-gbp.csv"));
        StackPanel importContent = new() { Spacing = 14 };
        importContent.Children.Add(new TextBlock { Text = "Synthetic fixtures", FontSize = 16, FontWeight = FontWeight.SemiBold });
        importContent.Children.Add(MutedText("Re-importing an existing transaction ID will not create a duplicate."));
        importContent.Children.Add(fixtureButtons);
        importContent.Children.Add(_importStatus);
        page.Children.Add(Card(importContent, new Thickness(18)));
        StackPanel notice = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
        notice.Children.Add(CreateIcon("shield", SuccessBrush, 22));
        notice.Children.Add(new TextBlock
        {
            Text = "Fictional inputs only · £1,200 card target · no network access or banking connections.",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        page.Children.Add(Card(notice, new Thickness(16)));
        return new ScrollViewer { Content = page };
    }

    private Grid BuildReviewPage()
    {
        Grid page = new() { ColumnDefinitions = new ColumnDefinitions("*,320") };
        Grid main = new() { Margin = new Thickness(24, 22, 18, 70), RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        main.Children.Add(PageHeader("STEP 2 OF 4", "Review transactions", "Check classifications before calculating the transfer plan.", _netTotal));
        Border filters = BuildFilterBar();
        filters.Margin = new Thickness(0, 18, 0, 10);
        Grid.SetRow(filters, 1);
        main.Children.Add(filters);
        Border ledgerContent = new()
        {
            Padding = new Thickness(0, 0, 16, 0),
            Child = _transactionList,
        };
        _ledgerScroll = new ScrollViewer
        {
            Content = ledgerContent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        _ledgerFrame = new Border
        {
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Child = _ledgerScroll,
        };
        _ledgerFrame.SizeChanged += (_, eventArgs) =>
            _transactionList.Width = Math.Max(700, eventArgs.NewSize.Width - 18);
        Grid.SetRow(_ledgerFrame, 2);
        main.Children.Add(_ledgerFrame);
        page.Children.Add(main);
        ScrollViewer inspectorScroll = new()
        {
            Content = BuildReviewInspector(),
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        _reviewInspector = new Border
        {
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Background = NeutralSoftBrush,
            Child = inspectorScroll,
        };
        Grid.SetColumn(_reviewInspector, 1);
        page.Children.Add(_reviewInspector);
        return page;
    }

    internal object ReviewLayoutEvidence()
    {
        Point ledgerOrigin = _ledgerFrame.TranslatePoint(default, this) ?? default;
        Point inspectorOrigin = _reviewInspector.TranslatePoint(default, this) ?? default;
        double viewportWidth = _ledgerFrame.Bounds.Width;
        double tableWidth = _transactionList.Bounds.Width + 16;
        return new
        {
            ledgerRight = ledgerOrigin.X + viewportWidth,
            inspectorLeft = inspectorOrigin.X,
            ledgerOverlapsInspector = ledgerOrigin.X + viewportWidth > inspectorOrigin.X + 0.5,
            viewportWidth,
            tableWidth,
            horizontalOverflow = tableWidth > viewportWidth + 0.5,
            horizontalScrollMode = _ledgerScroll.HorizontalScrollBarVisibility.ToString(),
            filterViewportWidth = _filterFrame.Bounds.Width,
            filterContentWidth = _filterContent.Bounds.Width,
            filterHorizontalOverflow = _filterContent.Bounds.Width > _filterFrame.Bounds.Width + 0.5,
            filterScrollMode = _filterScroll.HorizontalScrollBarVisibility.ToString(),
            filterClipsToFrame = _filterFrame.ClipToBounds,
        };
    }

    private Border BuildFilterBar()
    {
        _filterContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(10) };
        Grid searchContent = new() { ColumnDefinitions = new ColumnDefinitions("32,*") };
        searchContent.Children.Add(CreateIcon("search", MutedBrush, 15));
        Grid.SetColumn(_searchBox, 1);
        searchContent.Children.Add(_searchBox);
        Border search = new()
        {
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = searchContent,
        };
        _searchBox.BorderThickness = new Thickness(0);
        _searchBox.Background = Brushes.Transparent;
        _filterContent.Children.Add(search);
        _filterContent.Children.Add(_accountFilter);
        _filterContent.Children.Add(_categoryFilter);
        _filterContent.Children.Add(_statusFilter);
        _filterContent.Children.Add(_filterCount);
        _filterScroll = new ScrollViewer
        {
            Content = _filterContent,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        _filterFrame = new Border
        {
            Background = NeutralSoftBrush,
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Child = _filterScroll,
        };
        return _filterFrame;
    }

    private StackPanel BuildReviewInspector()
    {
        StackPanel inspector = new() { Margin = new Thickness(18, 22, 18, 70), Spacing = 14 };
        inspector.Children.Add(SectionLabel("REVIEW PROGRESS"));
        StackPanel progressContent = new() { Spacing = 8 };
        progressContent.Children.Add(_reviewProgress);
        progressContent.Children.Add(MutedText("Resolve flagged rows before confirming the transfer plan."));
        inspector.Children.Add(Card(progressContent, new Thickness(14)));
        inspector.Children.Add(_transactionDetails);
        inspector.Children.Add(SectionLabel("RUNNING TOTALS", new Thickness(0, 4, 0, 0)));
        inspector.Children.Add(Card(_sidebar, new Thickness(14)));
        inspector.Children.Add(BuildBalancesExpander());
        inspector.Children.Add(BuildRulesExpander());
        return inspector;
    }

    private Expander BuildBalancesExpander()
    {
        StackPanel content = new() { Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        foreach (Account account in Session.Accounts)
        {
            content.Children.Add(BalanceRow(account));
        }
        content.Children.Add(TargetRow());
        Button apply = ActionButton("Apply balances & target", "check", true);
        apply.HorizontalAlignment = HorizontalAlignment.Stretch;
        apply.Click += (_, _) => ApplyBalancesAndTarget();
        content.Children.Add(apply);
        return FramedExpander("Balances and card target", content);
    }

    private Expander BuildRulesExpander()
    {
        StackPanel content = new() { Spacing = 12, Margin = new Thickness(0, 10, 0, 0) };
        content.Children.Add(SubsectionTitle("Category rule"));
        ComboBox categoryBox = CompactComboBox(["Groceries", "Dining", "Travel"], 126);
        categoryBox.SelectedIndex = 0;
        ComboBox sourceBox = CompactComboBox(["Joint", "Shared Savings", "Personal"], 142);
        sourceBox.SelectedIndex = 2;
        content.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { categoryBox, sourceBox } });
        StackPanel ruleButtons = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        Button preview = ActionButton("Preview", "review");
        preview.Click += (_, _) =>
        {
            FundingSource source = ParseSource(sourceBox.SelectedItem as string);
            TransferPlan plan = Session.PreviewRuleChange(categoryBox.SelectedItem as string ?? string.Empty, source);
            _previewText.Text = DescribePreview(plan, source);
        };
        ruleButtons.Children.Add(preview);
        Button applyRule = ActionButton("Apply", "check", true);
        applyRule.Click += (_, _) =>
        {
            FundingSource source = ParseSource(sourceBox.SelectedItem as string);
            Session.ApplyCategoryRule(categoryBox.SelectedItem as string ?? string.Empty, source);
            _previewText.Text = "Rule applied.";
            Refresh();
        };
        ruleButtons.Children.Add(applyRule);
        content.Children.Add(ruleButtons);
        content.Children.Add(_previewText);
        content.Children.Add(new Separator { Background = LineBrush });
        content.Children.Add(SubsectionTitle("Allocation override"));
        ComboBox overrideBox = CompactComboBox(["Joint", "Shared Savings", "Personal"], 142);
        overrideBox.SelectedIndex = 0;
        content.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _overrideIdBox, overrideBox } });
        StackPanel overrideButtons = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        Button applyOverride = ActionButton("Apply override", "check", true);
        applyOverride.Click += (_, _) => ApplyOverride(overrideBox, false);
        overrideButtons.Children.Add(applyOverride);
        Button clearOverride = ActionButton("Clear", "close");
        clearOverride.Click += (_, _) => ApplyOverride(overrideBox, true);
        overrideButtons.Children.Add(clearOverride);
        content.Children.Add(overrideButtons);
        return FramedExpander("Rules and overrides", content);
    }

    private void ApplyOverride(ComboBox overrideBox, bool clear)
    {
        string id = _overrideIdBox.Text?.Trim() ?? string.Empty;
        if (id.Length == 0)
        {
            _previewText.Text = "Choose a transaction to override.";
            return;
        }
        try
        {
            Session.SetOverride(id, clear ? null : ParseSource(overrideBox.SelectedItem as string));
            _previewText.Text = clear ? $"Override cleared for {id}." : $"Override applied to {id}.";
            Refresh();
        }
        catch (InvalidOperationException exception)
        {
            _previewText.Text = exception.Message;
        }
    }

    private ScrollViewer BuildTransfersPage()
    {
        StackPanel page = PageStack();
        page.Children.Add(PageHeader("STEP 3 OF 4", "Transfer plan", "Review calculated movements and projected balances before acting outside the app.", null));
        page.Children.Add(Card(_transfersPanel, new Thickness(18)));
        return new ScrollViewer { Content = page };
    }

    private ScrollViewer BuildAcknowledgePage()
    {
        StackPanel page = PageStack();
        page.Children.Add(PageHeader("STEP 4 OF 4", "Confirm transfers", "Perform transfers outside the app, then acknowledge each one here.", null));
        page.Children.Add(Card(_ackPanel, new Thickness(18)));
        page.Children.Add(Card(_persistenceStatus, new Thickness(14)));
        return new ScrollViewer { Content = page };
    }

    private ScrollViewer BuildHistoryPage()
    {
        StackPanel page = PageStack();
        page.Children.Add(PageHeader("ARCHIVE", "Reconciliation history", "Review completed sessions and their acknowledged transfers.", null));
        page.Children.Add(Card(_historyPanel, new Thickness(18)));
        return new ScrollViewer { Content = page };
    }

    private Button ImportButton(string label, string file)
    {
        Button button = ActionButton(label, "upload", true);
        button.Click += (_, _) =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", file);
            ImportResult result = Session.Import(path);
            string queued = Session.PendingTransactionCount > 0 ? $" {Session.PendingTransactionCount} transaction(s) queued for the next reconciliation." : string.Empty;
            string corrections = result.CorrectionsQueued > 0 ? $" {result.CorrectionsQueued} changed locked/settled transaction(s) require adjustment review." : string.Empty;
            _importStatus.Text = $"Imported {Path.GetFileName(file)}: {result.Added} added, {result.Updated} updated, {result.Skipped} unchanged duplicate(s) skipped.{queued}{corrections}";
            Refresh();
        };
        return button;
    }

    private void WireFilters()
    {
        _searchBox.TextChanged += (_, _) => RefreshTransactions();
        _accountFilter.SelectionChanged += (_, _) => RefreshTransactions();
        _categoryFilter.SelectionChanged += (_, _) => RefreshTransactions();
        _statusFilter.SelectionChanged += (_, _) => RefreshTransactions();
    }

    private void SelectStage(int index)
    {
        _selectedStage = index;
        _workspace.Content = _pages[index];
        string[] labels = ["Import", "Review", "Transfers", "Confirm", "History"];
        _breadcrumb.Text = $"September reconciliation  /  {labels[index]}";
        for (int stage = 0; stage < _stageButtons.Count; stage++)
        {
            bool selected = stage == index;
            _stageButtons[stage].Background = selected ? AccentSoftBrush : Brushes.Transparent;
            if (selected)
            {
                _stageButtons[stage].Foreground = AccentBrush;
            }
            else
            {
                _stageButtons[stage].ClearValue(ForegroundProperty);
            }
        }
    }

    private void ApplyThemeSelection()
    {
        string selection = _themeBox.SelectedItem as string ?? "System";
        if (!_suppressThemeSave)
        {
            ThemeSettings.Save(selection);
        }
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = App.ToVariant(selection);
        }
    }

    private Grid BalanceRow(Account account)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(new TextBlock
        {
            Text = $"{account.Name}  ·  min {account.ProtectedMinimum.ToString("C0", BritishCulture)}",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14,
        });
        Grid.SetColumn(_balanceBoxes[account.Name], 1);
        row.Children.Add(_balanceBoxes[account.Name]);
        return row;
    }

    private Grid TargetRow()
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(new TextBlock { Text = "Card payment target", VerticalAlignment = VerticalAlignment.Center, FontSize = 14 });
        Grid.SetColumn(_targetBox, 1);
        row.Children.Add(_targetBox);
        return row;
    }

    private void ApplyBalancesAndTarget()
    {
        foreach ((string name, TextBox box) in _balanceBoxes)
        {
            if (decimal.TryParse(box.Text, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out decimal value))
            {
                Session.SetBalance(name, value);
            }
        }
        if (Session.Draft?.IsLocked != true
            && decimal.TryParse(_targetBox.Text, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out decimal target))
        {
            Session.SetCardPaymentTarget(target);
        }
        Refresh();
    }

    private async Task SaveScenarioAsync()
    {
        string? password = await RequestPasswordAsync("Save encrypted draft", "Save draft");
        if (password is null)
        {
            return;
        }

        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dot-finance", "experiment-03");
            Directory.CreateDirectory(directory);
            string databasePath = Path.Combine(directory, "working.db");
            using PrototypeVault vault = File.Exists(databasePath)
                ? PrototypeVault.Unlock(databasePath, password)
                : PrototypeVault.Create(databasePath, password);
            vault.SaveScenario(Session.ToState());
            _persistenceStatus.Text = $"Saved {Session.Allocated.Count} transactions to {databasePath}.";
        }
        catch (Exception exception)
        {
            _persistenceStatus.Text = $"Save failed: {exception.Message}";
        }
    }

    private async Task LoadScenarioAsync()
    {
        string databasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dot-finance", "experiment-03", "working.db");
        if (!File.Exists(databasePath))
        {
            _persistenceStatus.Text = "No saved scenario yet.";
            return;
        }

        string? password = await RequestPasswordAsync("Open encrypted draft", "Open draft");
        if (password is null)
        {
            return;
        }

        try
        {
            using PrototypeVault vault = PrototypeVault.Unlock(databasePath, password);
            Session.FromState(vault.LoadScenario());
            _targetBox.Text = Session.CardPaymentTarget.ToString("0.00", CultureInfo.InvariantCulture);
            foreach ((string name, TextBox box) in _balanceBoxes)
            {
                Account account = Session.Accounts.First(account => account.Name == name);
                box.Text = account.CurrentBalance.ToString("0.00", CultureInfo.InvariantCulture);
            }
            _persistenceStatus.Text = $"Reopened scenario with {Session.Allocated.Count} transactions.";
            Refresh();
        }
        catch (Exception exception)
        {
            _persistenceStatus.Text = $"Reopen failed: {exception.Message}";
        }
    }

    private async Task<string?> RequestPasswordAsync(string title, string actionLabel)
    {
        TextBox passwordBox = CompactTextBox(string.Empty, 320);
        passwordBox.PlaceholderText = "Encryption password";
        passwordBox.PasswordChar = '●';

        Window dialog = new()
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        StackPanel content = new() { Margin = new Thickness(24), Spacing = 14 };
        content.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.Bold });
        content.Children.Add(MutedText("The password encrypts this local draft. It is not stored by dot-finance."));
        content.Children.Add(passwordBox);
        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        Button cancel = ActionButton("Cancel", "close");
        cancel.Click += (_, _) => dialog.Close(null);
        actions.Children.Add(cancel);
        Button confirm = ActionButton(actionLabel, actionLabel.StartsWith("Open", StringComparison.Ordinal) ? "history" : "save", true);
        confirm.IsEnabled = false;
        confirm.Click += (_, _) => dialog.Close(passwordBox.Text);
        actions.Children.Add(confirm);
        content.Children.Add(actions);
        passwordBox.TextChanged += (_, _) => confirm.IsEnabled = !string.IsNullOrWhiteSpace(passwordBox.Text);
        passwordBox.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Enter && confirm.IsEnabled)
            {
                dialog.Close(passwordBox.Text);
            }
        };
        dialog.Opened += (_, _) => passwordBox.Focus();
        dialog.Content = content;
        return await dialog.ShowDialog<string?>(this);
    }

    /// <summary>Imports the base demonstration fixture and opens the Review stage for screenshot capture.</summary>
    public void PrepareWorkflowCapture()
    {
        Session.Import(Path.Combine(AppContext.BaseDirectory, "Fixtures", "synthetic-reconciliation.csv"));
        SelectStage(1);
        Refresh();
    }

    public void ApplyWorkflowCaptureCorrection()
    {
        Session.CorrectTransaction("R-0005", "Groceries", string.Empty);
        Refresh();
    }

    public void SetThemeForCapture(string theme)
    {
        _suppressThemeSave = true;
        try
        {
            _themeBox.SelectedItem = theme;
            Application.Current!.RequestedThemeVariant = App.ToVariant(theme);
        }
        finally
        {
            _suppressThemeSave = false;
        }
    }

    private void Refresh()
    {
        RefreshFilterOptions();
        RefreshTransactions();
        RefreshSidebar();
        RefreshTransfers();
        RefreshAcknowledgements();
        RefreshHistory();
        _targetBox.IsEnabled = Session.Draft?.IsLocked != true;
        int transactionReviewCount = Session.Allocated.Count(transaction => transaction.RequiresReview);
        int reviewCount = transactionReviewCount + Session.PendingCorrections.Count;
        _sessionReviewCount.Text = reviewCount == 0 ? "Everything is ready" : $"{reviewCount} item(s) need review";
        _sessionReviewCount.Foreground = reviewCount == 0 ? SuccessBrush : WarningBrush;
        _reviewProgress.Text = $"{Session.Allocated.Count - transactionReviewCount} of {Session.Allocated.Count} ready";
        if (reviewCount == 0)
        {
            _reviewProgress.Foreground = SuccessBrush;
        }
        else
        {
            _reviewProgress.ClearValue(ForegroundProperty);
        }
        decimal net = Session.Allocated
            .Where(transaction => transaction.FundingSource is not null || transaction.RequiresReview)
            .Sum(transaction => -ReconciliationEngine.EffectiveGbpAmount(transaction));
        _netTotal.Text = Pounds(net);
    }

    private void RefreshFilterOptions()
    {
        _updatingFilters = true;
        try
        {
            string account = _accountFilter.SelectedItem as string ?? "All accounts";
            string category = _categoryFilter.SelectedItem as string ?? "All categories";
            string[] accounts = ["All accounts", .. Session.Allocated.Select(transaction => transaction.Account).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            string[] categories = ["All categories", .. Session.Allocated.Select(transaction => transaction.Category).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            _accountFilter.ItemsSource = accounts;
            _categoryFilter.ItemsSource = categories;
            _accountFilter.SelectedItem = accounts.Contains(account, StringComparer.Ordinal) ? account : "All accounts";
            _categoryFilter.SelectedItem = categories.Contains(category, StringComparer.Ordinal) ? category : "All categories";
            string? selectedOverride = _overrideIdBox.Text;
            string[] transactionIds = [.. Session.Allocated.Select(transaction => transaction.Id)];
            _overrideIdBox.ItemsSource = transactionIds;
            if (selectedOverride is not null && transactionIds.Contains(selectedOverride, StringComparer.Ordinal))
            {
                _overrideIdBox.SelectedItem = selectedOverride;
            }
        }
        finally
        {
            _updatingFilters = false;
        }
    }

    private void RefreshTransactions()
    {
        if (_updatingFilters)
        {
            return;
        }
        IReadOnlyList<Transaction> visible = FilteredTransactions();
        if (visible.Count > 0 && visible.All(transaction => transaction.Id != _selectedTransactionId))
        {
            SetSelectedTransaction(visible[0], false);
        }
        _transactionList.Children.Clear();
        _transactionList.Children.Add(BuildLedgerHeader());
        foreach (Transaction transaction in visible)
        {
            _transactionList.Children.Add(BuildTransactionRow(transaction));
        }
        if (visible.Count == 0)
        {
            _transactionList.Children.Add(new TextBlock
            {
                Text = "No transactions match these filters.",
                Foreground = MutedBrush,
                Margin = new Thickness(18, 28),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        _filterCount.Text = $"{visible.Count} of {Session.Allocated.Count}";
        RefreshTransactionDetails();
    }

    private IReadOnlyList<Transaction> FilteredTransactions()
    {
        string query = _searchBox.Text?.Trim() ?? string.Empty;
        string account = _accountFilter.SelectedItem as string ?? "All accounts";
        string category = _categoryFilter.SelectedItem as string ?? "All categories";
        string status = _statusFilter.SelectedItem as string ?? "All statuses";
        return [.. Session.Allocated.Where(transaction =>
            (query.Length == 0 || TransactionSearchText(transaction).Contains(query, StringComparison.OrdinalIgnoreCase))
            && (account == "All accounts" || transaction.Account == account)
            && (category == "All categories" || transaction.Category == category)
            && (status == "All statuses" || TransactionStatus(transaction) == status))];
    }

    private static string TransactionSearchText(Transaction transaction) =>
        $"{transaction.Id} {transaction.Merchant} {transaction.Counterparty} {transaction.CustomName} {transaction.Notes} {transaction.AdditionalDetails} {transaction.Category} {transaction.Subcategory}";

    private static Grid BuildLedgerHeader()
    {
        Grid header = LedgerGrid();
        header.Background = NeutralSoftBrush;
        header.Children.Add(LedgerHeading("DATE", 0));
        header.Children.Add(LedgerHeading("TRANSACTION", 1));
        header.Children.Add(LedgerHeading("ACCOUNT", 2));
        header.Children.Add(LedgerHeading("CATEGORY", 3));
        header.Children.Add(LedgerHeading("STATUS", 4));
        TextBlock amount = LedgerHeading("AMOUNT", 5);
        amount.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(amount);
        return header;
    }

    private Border BuildTransactionRow(Transaction transaction)
    {
        Grid row = LedgerGrid();
        row.MinHeight = 61;
        row.ClipToBounds = true;
        row.Children.Add(LedgerCell(transaction.Date.ToString("dd MMM", BritishCulture), 0, true));
        Grid merchant = new()
        {
            ColumnDefinitions = new ColumnDefinitions("31,*"),
            Margin = new Thickness(10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
        };
        merchant.Children.Add(new Border
        {
            Width = 31,
            Height = 31,
            Background = NeutralRaisedBrush,
            CornerRadius = new CornerRadius(8),
            Child = CreateIcon(CategoryIcon(transaction), MutedBrush, 15),
        });
        StackPanel merchantText = new()
        {
            Spacing = 2,
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
        };
        merchantText.Children.Add(new TextBlock
        {
            Text = TransactionName(transaction),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        merchantText.Children.Add(new TextBlock
        {
            Text = $"{transaction.Id} · {TransactionNote(transaction)}",
            Foreground = MutedBrush,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(merchantText, 1);
        merchant.Children.Add(merchantText);
        Grid.SetColumn(merchant, 1);
        row.Children.Add(merchant);
        Grid account = new()
        {
            ColumnDefinitions = new ColumnDefinitions("14,*"),
            Margin = new Thickness(10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
        };
        account.Children.Add(CreateIcon("card", AccentBrush, 14));
        TextBlock accountName = new()
        {
            Text = transaction.Account,
            FontSize = 13,
            Margin = new Thickness(7, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(accountName, 1);
        account.Children.Add(accountName);
        Grid.SetColumn(account, 2);
        row.Children.Add(account);
        Grid category = new()
        {
            ColumnDefinitions = new ColumnDefinitions("7,*"),
            Margin = new Thickness(10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
        };
        category.Children.Add(new Border
        {
            Width = 7,
            Height = 7,
            Background = CategoryBrush(transaction.Category),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
        });
        TextBlock categoryName = new()
        {
            Text = string.IsNullOrWhiteSpace(transaction.Category)
                ? "Uncategorised"
                : string.IsNullOrWhiteSpace(transaction.Subcategory) ? transaction.Category : $"{transaction.Category} / {transaction.Subcategory}",
            FontSize = 13,
            Margin = new Thickness(6, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(categoryName, 1);
        category.Children.Add(categoryName);
        Grid.SetColumn(category, 3);
        row.Children.Add(category);
        Border status = StatusPill(TransactionStatus(transaction));
        status.HorizontalAlignment = HorizontalAlignment.Left;
        status.VerticalAlignment = VerticalAlignment.Center;
        status.Margin = new Thickness(10, 0);
        Grid.SetColumn(status, 4);
        row.Children.Add(status);
        TextBlock amount = new()
        {
            Text = SignedPounds(transaction.Amount),
            FontFamily = MonospaceFont,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 14, 0),
        };
        if (transaction.Amount > 0)
        {
            amount.Foreground = SuccessBrush;
        }
        Grid.SetColumn(amount, 5);
        row.Children.Add(amount);
        Button selection = new()
        {
            Content = row,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            Background = transaction.Id == _selectedTransactionId ? AccentSoftBrush : Brushes.Transparent,
            BorderThickness = transaction.Id == _selectedTransactionId ? new Thickness(3, 0, 0, 0) : new Thickness(0),
            BorderBrush = AccentBrush,
            CornerRadius = new CornerRadius(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        AutomationProperties.SetName(selection, $"Select {TransactionName(transaction)} transaction");
        selection.Click += (_, _) => SetSelectedTransaction(transaction, true);
        return new Border { BorderBrush = LineBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = selection };
    }

    private void SetSelectedTransaction(Transaction transaction, bool refreshRows)
    {
        _selectedTransactionId = transaction.Id;
        _overrideIdBox.SelectedItem = transaction.Id;
        if (refreshRows)
        {
            RefreshTransactions();
        }
        else
        {
            RefreshTransactionDetails();
        }
    }

    private void RefreshTransactionDetails()
    {
        _transactionDetails.Content = null;
        Transaction? selected = Session.Allocated.FirstOrDefault(transaction => transaction.Id == _selectedTransactionId);
        if (selected is null)
        {
            _transactionDetails.Content = Card(MutedText("Import a fixture, then select a transaction to inspect it."), new Thickness(14));
            return;
        }
        StackPanel content = new() { Spacing = 12 };
        Grid heading = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        StackPanel title = new() { Spacing = 3 };
        title.Children.Add(SectionLabel("SELECTED TRANSACTION"));
        title.Children.Add(new TextBlock
        {
            Text = TransactionName(selected),
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        heading.Children.Add(title);
        TextBlock amount = new()
        {
            Text = selected.Currency.Equals("GBP", StringComparison.OrdinalIgnoreCase)
                ? SignedPounds(selected.Amount)
                : selected.ActualGbpAmount is { } actual
                    ? $"{SignedPounds(actual)} GBP"
                    : $"{selected.Amount:0.00} {selected.Currency}",
            FontFamily = MonospaceFont,
            FontSize = 19,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        if (selected.Amount > 0)
        {
            amount.Foreground = SuccessBrush;
        }
        Grid.SetColumn(amount, 1);
        heading.Children.Add(amount);
        content.Children.Add(heading);
        content.Children.Add(StatusPill(TransactionStatus(selected)));
        Grid facts = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        facts.Children.Add(DetailField("ACCOUNT", selected.Account, 0, 0));
        facts.Children.Add(DetailField("DATE", selected.Date.ToString("dd MMM yyyy", BritishCulture), 1, 0));
        facts.Children.Add(DetailField("CATEGORY", string.IsNullOrWhiteSpace(selected.Category) ? "Uncategorised" : selected.Category, 0, 1));
        facts.Children.Add(DetailField("SUBCATEGORY", string.IsNullOrWhiteSpace(selected.Subcategory) ? "—" : selected.Subcategory, 1, 1));
        content.Children.Add(facts);
        content.Children.Add(DetailField("IMPORT NOTE", TransactionNote(selected), 0, 0));
        if (!selected.Currency.Equals("GBP", StringComparison.OrdinalIgnoreCase))
        {
            StackPanel conversion = new() { Spacing = 7 };
            conversion.Children.Add(SubsectionTitle("Actual GBP charged amount"));
            TextBox actualGbpBox = CompactTextBox(
                selected.ActualGbpAmount?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty,
                double.NaN);
            actualGbpBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            actualGbpBox.PlaceholderText = "Signed GBP amount";
            conversion.Children.Add(actualGbpBox);
            Button recordGbp = ActionButton("Record GBP amount", "check", true);
            recordGbp.HorizontalAlignment = HorizontalAlignment.Stretch;
            recordGbp.Click += (_, _) => ApplyActualGbpAmount(selected.Id, actualGbpBox.Text);
            conversion.Children.Add(recordGbp);
            content.Children.Add(conversion);
        }
        StackPanel correction = new() { Spacing = 7 };
        correction.Children.Add(SubsectionTitle("Accept correction"));
        TextBox categoryBox = CompactTextBox(selected.Category, double.NaN);
        categoryBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        categoryBox.PlaceholderText = "Category";
        TextBox subcategoryBox = CompactTextBox(selected.Subcategory, double.NaN);
        subcategoryBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        subcategoryBox.PlaceholderText = "Subcategory";
        correction.Children.Add(categoryBox);
        correction.Children.Add(subcategoryBox);
        Button apply = ActionButton("Accept correction", "check", true);
        apply.HorizontalAlignment = HorizontalAlignment.Stretch;
        apply.Click += (_, _) => ApplyTransactionCorrection(selected.Id, categoryBox.Text, subcategoryBox.Text);
        correction.Children.Add(apply);
        content.Children.Add(correction);
        _transactionDetails.Content = Card(content, new Thickness(14));
    }

    private void ApplyTransactionCorrection(string transactionId, string? category, string? subcategory)
    {
        try
        {
            Session.CorrectTransaction(
                transactionId,
                category ?? string.Empty,
                subcategory ?? string.Empty);
            _previewText.Text = $"Accepted correction for {transactionId}.";
            Refresh();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _previewText.Text = exception.Message;
        }
    }

    private void ApplyActualGbpAmount(string transactionId, string? value)
    {
        try
        {
            if (!decimal.TryParse(value, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out decimal amount))
            {
                throw new ArgumentException("Enter the signed GBP amount actually charged.");
            }

            Session.SetActualGbpAmount(transactionId, amount);
            _previewText.Text = $"Recorded the actual GBP amount for {transactionId}.";
            Refresh();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _previewText.Text = exception.Message;
        }
    }

    private void RefreshSidebar()
    {
        _sidebar.Children.Clear();
        if (Session.PendingCorrections.Count > 0)
        {
            _sidebar.Children.Add(new TextBlock
            {
                Text = $"{Session.PendingCorrections.Count} changed locked/settled transaction(s) await explicit adjustment review.",
                Foreground = WarningBrush,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            foreach (PendingTransactionCorrection correction in Session.PendingCorrections)
            {
                Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                row.Children.Add(MutedText($"{correction.Original.Id} · {correction.Original.Amount:0.00} → {correction.Updated.Amount:0.00}"));
                Button accept = ActionButton(Session.Draft?.IsLocked == true ? "Next draft" : "Apply adjustment", "check");
                accept.IsEnabled = Session.Draft?.IsLocked != true;
                accept.Click += (_, _) =>
                {
                    try
                    {
                        Session.AcceptPendingCorrection(correction.Original.Id);
                        _previewText.Text = $"Accepted {correction.Original.Id} as a reversal and replacement adjustment.";
                        Refresh();
                    }
                    catch (InvalidOperationException exception)
                    {
                        _previewText.Text = exception.Message;
                    }
                };
                Grid.SetColumn(accept, 1);
                row.Children.Add(accept);
                _sidebar.Children.Add(row);
            }
            _sidebar.Children.Add(new Separator { Background = LineBrush });
        }
        foreach (FundingSource source in new[] { FundingSource.Joint, FundingSource.SharedSavings, FundingSource.Personal })
        {
            List<Transaction> matches = [.. Session.Allocated.Where(transaction => transaction.FundingSource == source)];
            if (matches.Count == 0)
            {
                continue;
            }
            _sidebar.Children.Add(SummaryRow(SourceLabel(source), Pounds(matches.Sum(transaction => -transaction.Amount)), true));
            foreach (IGrouping<string, Transaction> category in matches.GroupBy(transaction => transaction.Category).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                _sidebar.Children.Add(SummaryRow(category.Key, Pounds(category.Sum(transaction => -transaction.Amount)), false));
            }
        }
        foreach (ReportingSubtotal subtotal in Session.Plan.ReportingSubtotals)
        {
            _sidebar.Children.Add(SummaryRow($"{subtotal.Name} · separate", Pounds(subtotal.Amount), false));
        }
        List<Transaction> review = [.. Session.Allocated.Where(transaction => transaction.RequiresReview)];
        if (review.Count > 0)
        {
            _sidebar.Children.Add(SummaryRow($"Requires review · {review.Count}", Pounds(review.Sum(transaction => -transaction.Amount)), true));
        }
        if (_sidebar.Children.Count == 0)
        {
            _sidebar.Children.Add(MutedText("Totals appear after import."));
        }
    }

    private void RefreshTransfers()
    {
        _transfersPanel.Children.Clear();
        IReadOnlyList<Transfer> transfers = Session.Draft?.Transfers ?? Session.Plan.Transfers;
        foreach (Transfer transfer in transfers)
        {
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("42,*,Auto"), MinHeight = 54 };
            row.Children.Add(new Border
            {
                Width = 32,
                Height = 32,
                Background = AccentSoftBrush,
                CornerRadius = new CornerRadius(8),
                Child = CreateIcon("arrow", AccentBrush, 15),
                VerticalAlignment = VerticalAlignment.Center,
            });
            StackPanel description = new() { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            description.Children.Add(new TextBlock { Text = transfer.Source, FontWeight = FontWeight.SemiBold });
            description.Children.Add(MutedText(transfer.Kind.ToString()));
            Grid.SetColumn(description, 1);
            row.Children.Add(description);
            TextBlock amount = new() { Text = Pounds(transfer.Amount), FontFamily = MonospaceFont, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(amount, 2);
            row.Children.Add(amount);
            _transfersPanel.Children.Add(row);
            _transfersPanel.Children.Add(new Separator { Background = LineBrush });
        }
        foreach (AccountBalance balance in Session.Plan.ProjectedBalances)
        {
            _transfersPanel.Children.Add(SummaryRow($"{balance.AccountName} projected", Pounds(balance.ProjectedBalance), false));
        }
        _transfersPanel.Children.Add(SummaryRow("Target discrepancy", Pounds(Session.Draft?.Discrepancy ?? 0m), true));
        foreach (Shortfall shortfall in Session.Plan.Shortfalls)
        {
            _transfersPanel.Children.Add(new TextBlock
            {
                Text = $"Shortfall · {shortfall.AccountName} needs {Pounds(shortfall.Amount)} more",
                Foreground = WarningBrush,
                FontWeight = FontWeight.SemiBold,
            });
        }
    }

    private void RefreshAcknowledgements()
    {
        _ackPanel.Children.Clear();
        ReconciliationDraft? draft = Session.Draft;
        IReadOnlyList<Transfer> funding = [.. (draft?.Transfers ?? Session.Plan.Transfers).Where(transfer => transfer.Kind != TransferKind.CardPayment)];
        bool allAcknowledged = funding.Count > 0 && funding.All(transfer => transfer.Acknowledged);
        string state = draft?.IsSettled == true ? "Settled" : draft?.IsLocked == true ? "Locked" : "Draft · editable";
        _ackPanel.Children.Add(StatusPill(state));
        if (draft is { Discrepancy: not 0m, DiscrepancyAcknowledged: false, IsLocked: false })
        {
            Grid discrepancy = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            discrepancy.Children.Add(new TextBlock
            {
                Text = $"Payment target differs from imported spending by {Pounds(draft.Discrepancy)}.",
                Foreground = WarningBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
            Button acknowledge = ActionButton("Acknowledge discrepancy", "check");
            acknowledge.Click += (_, _) => { Session.AcknowledgeDiscrepancy(); Refresh(); };
            Grid.SetColumn(acknowledge, 1);
            discrepancy.Children.Add(acknowledge);
            _ackPanel.Children.Add(discrepancy);
        }
        else if (draft?.DiscrepancyAcknowledged == true && draft.Discrepancy != 0m)
        {
            _ackPanel.Children.Add(new TextBlock { Text = $"Discrepancy acknowledged · {Pounds(draft.Discrepancy)}", Foreground = SuccessBrush });
        }
        foreach (Transfer transfer in funding)
        {
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 46 };
            StackPanel description = new() { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            description.Children.Add(new TextBlock { Text = $"{transfer.Source} · {Pounds(transfer.Amount)}", FontWeight = FontWeight.SemiBold });
            description.Children.Add(MutedText(transfer.Acknowledged ? "Acknowledged" : "Outstanding"));
            row.Children.Add(description);
            bool blocked = Session.Plan.Shortfalls.Any(shortfall => shortfall.AccountName == transfer.Source
                || shortfall.AccountName == "Personal"
                    && Session.Accounts.Any(account => account.Name == transfer.Source
                        && account.Role is AccountRole.PrimaryPersonal or AccountRole.PersonalSavings));
            bool discrepancyBlocked = !transfer.Acknowledged
                && draft is { Discrepancy: not 0m, DiscrepancyAcknowledged: false };
            bool reviewBlocked = !transfer.Acknowledged
                && (Session.Allocated.Any(transaction => transaction.RequiresReview)
                    || draft?.IsLocked != true && Session.PendingCorrections.Count > 0);
            bool actionBlocked = blocked || discrepancyBlocked || reviewBlocked;
            Button button = ActionButton(transfer.Acknowledged ? "Undo" : actionBlocked ? "Blocked" : "Acknowledge", transfer.Acknowledged ? "history" : "check", !transfer.Acknowledged && !actionBlocked);
            button.IsEnabled = transfer.Acknowledged || !actionBlocked;
            button.Click += (_, _) =>
            {
                if (transfer.Acknowledged)
                {
                    Session.UndoTransferAcknowledgement(transfer.Source, transfer.Kind);
                }
                else
                {
                    Session.AcknowledgeTransfer(transfer.Source, transfer.Kind);
                }
                Refresh();
            };
            Grid.SetColumn(button, 1);
            row.Children.Add(button);
            _ackPanel.Children.Add(row);
            _ackPanel.Children.Add(new Separator { Background = LineBrush });
        }
        if (Session.Plan.Shortfalls.Count > 0)
        {
            _ackPanel.Children.Add(new TextBlock { Text = "A shortfall blocks confirmation of the affected transfer.", Foreground = WarningBrush });
        }
        bool canPay = allAcknowledged
            && Session.Plan.Shortfalls.Count == 0
            && !Session.Allocated.Any(transaction => transaction.RequiresReview)
            && Session.PendingCorrections.Count == 0
            && draft?.IsSettled != true
            && draft is not { Discrepancy: not 0m, DiscrepancyAcknowledged: false };
        Button pay = ActionButton(draft?.IsSettled == true ? "Card paid" : "Confirm card paid", "card", true);
        pay.IsEnabled = canPay;
        pay.HorizontalAlignment = HorizontalAlignment.Left;
        pay.Click += (_, _) => { Session.AcknowledgeCardPaid(); Refresh(); };
        _ackPanel.Children.Add(pay);
    }

    private void RefreshHistory()
    {
        _historyPanel.Children.Clear();
        if (Session.CompletedReconciliations.Count == 0)
        {
            _historyPanel.Children.Add(MutedText("No completed reconciliation yet."));
            return;
        }
        foreach ((ReconciliationDraft reconciliation, int index) in Session.CompletedReconciliations.Select((item, index) => (item, index)))
        {
            StackPanel item = new() { Spacing = 5 };
            item.Children.Add(new TextBlock
            {
                Text = $"Reconciliation {index + 1} · {Pounds(reconciliation.CardPaymentTarget)}",
                FontWeight = FontWeight.SemiBold,
            });
            item.Children.Add(MutedText($"{reconciliation.Transactions.Count} settled transactions · {reconciliation.Transfers.Count(transfer => transfer.Acknowledged)} acknowledgements"));
            foreach (Transfer transfer in reconciliation.Transfers)
            {
                item.Children.Add(MutedText($"{transfer.Source} · {transfer.Kind} · {Pounds(transfer.Amount)}"));
            }
            if (index == Session.CompletedReconciliations.Count - 1)
            {
                Button reopen = ActionButton("Reopen latest record", "history");
                reopen.Click += (_, _) =>
                {
                    try
                    {
                        Session.ReopenLatestCompleted();
                        _persistenceStatus.Text = "Reopened the latest completion. This changes the record only; it does not reverse external transfers or card payments.";
                        Refresh();
                    }
                    catch (InvalidOperationException exception)
                    {
                        _persistenceStatus.Text = exception.Message;
                    }
                };
                item.Children.Add(reopen);
            }
            _historyPanel.Children.Add(Card(item, new Thickness(14)));
        }
    }

    private static StackPanel PageStack() => new() { Margin = new Thickness(28, 24, 28, 70), Spacing = 16 };

    private static Grid PageHeader(string eyebrow, string title, string subtitle, Control? trailing)
    {
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        StackPanel text = new() { Spacing = 4 };
        text.Children.Add(SectionLabel(eyebrow));
        text.Children.Add(new TextBlock { Text = title, FontSize = 30, FontWeight = FontWeight.Bold });
        text.Children.Add(MutedText(subtitle));
        header.Children.Add(text);
        if (trailing is not null)
        {
            Grid.SetColumn(trailing, 1);
            trailing.VerticalAlignment = VerticalAlignment.Bottom;
            header.Children.Add(trailing);
        }
        return header;
    }

    private static Border Card(Control content, Thickness padding) => new()
    {
        Background = NeutralSoftBrush,
        BorderBrush = LineBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = padding,
        Child = content,
    };

    private static Expander FramedExpander(string title, Control content) => new()
    {
        Header = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14 },
        Content = content,
        Padding = new Thickness(12, 8),
        Background = NeutralSoftBrush,
        BorderBrush = LineBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
    };

    private static TextBlock SectionLabel(string text, Thickness? margin = null) => new()
    {
        Text = text,
        Foreground = AccentBrush,
        FontSize = 11,
        FontWeight = FontWeight.Bold,
        LetterSpacing = 1.2,
        Margin = margin ?? new Thickness(0),
    };

    private static TextBlock SubsectionTitle(string text) => new() { Text = text, FontSize = 14, FontWeight = FontWeight.SemiBold };

    private static TextBlock MutedText(string text) => new()
    {
        Text = text,
        Foreground = MutedBrush,
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
    };

    private static Button ActionButton(string text, string icon, bool primary = false)
    {
        IBrush primaryForeground = new SolidColorBrush(Color.Parse("#2D071A"));
        StackPanel content = new() { Orientation = Orientation.Horizontal, Spacing = 7 };
        content.Children.Add(CreateIcon(icon, primary ? primaryForeground : null, 16));
        content.Children.Add(new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Button button = new()
        {
            Content = content,
            MinHeight = 38,
            Padding = new Thickness(12, 6),
            Background = primary ? AccentBrush : NeutralRaisedBrush,
            BorderBrush = primary ? AccentBrush : LineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        if (primary)
        {
            button.Foreground = primaryForeground;
        }

        return button;
    }

    private static TextBox CompactTextBox(string text, double width) => new()
    {
        Text = text,
        Width = width,
        MinHeight = 38,
        CornerRadius = new CornerRadius(8),
        FontSize = 14,
        Padding = new Thickness(10, 0),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static ComboBox CompactComboBox(IEnumerable<string> items, double width) => new()
    {
        ItemsSource = items,
        Width = width,
        MinHeight = 38,
        CornerRadius = new CornerRadius(8),
        FontSize = 14,
        Padding = new Thickness(10, 0),
        VerticalContentAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Left,
    };

    private static Grid LedgerGrid() => new()
    {
        ColumnDefinitions = new ColumnDefinitions("76,*,135,130,96,108"),
    };

    private static TextBlock LedgerHeading(string text, int column)
    {
        TextBlock heading = new()
        {
            Text = text,
            Foreground = MutedBrush,
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            LetterSpacing = 1,
            Margin = new Thickness(12, 11),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(heading, column);
        return heading;
    }

    private static TextBlock LedgerCell(string text, int column, bool mono = false)
    {
        TextBlock cell = new()
        {
            Text = text,
            Foreground = MutedBrush,
            FontSize = 13,
            FontFamily = mono ? MonospaceFont : FontFamily.Default,
            Margin = new Thickness(12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(cell, column);
        return cell;
    }

    private static Border StatusPill(string status)
    {
        (IBrush foreground, IBrush background) = status switch
        {
            "Ready" or "Balanced" or "Settled" => (SuccessBrush, SuccessSoftBrush),
            "Review" or "Blocked" => (WarningBrush, WarningSoftBrush),
            "Excluded" => (VioletBrush, NeutralSoftBrush),
            _ => (MutedBrush, NeutralSoftBrush),
        };
        StackPanel content = new() { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new Border
        {
            Width = 6,
            Height = 6,
            Background = foreground,
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = status,
            Foreground = foreground,
            FontSize = 12,
            LineHeight = 14,
            Margin = new Thickness(0, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return new Border
        {
            Background = background,
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            MinHeight = 24,
            Padding = new Thickness(8, 2),
            Child = content,
        };
    }

    private static Control DetailField(string label, string value, int column, int row)
    {
        StackPanel field = new() { Spacing = 4, Margin = new Thickness(0, 0, 6, 12) };
        field.Children.Add(new TextBlock { Text = label, Foreground = MutedBrush, FontSize = 10, FontWeight = FontWeight.Bold, LetterSpacing = 1 });
        field.Children.Add(new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(field, column);
        Grid.SetRow(field, row);
        return field;
    }

    private static Grid SummaryRow(string label, string value, bool emphasized)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 35 };
        TextBlock labelText = new()
        {
            Text = label,
            FontSize = 13,
            FontWeight = emphasized ? FontWeight.SemiBold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (!emphasized)
        {
            labelText.Foreground = MutedBrush;
        }

        row.Children.Add(labelText);
        TextBlock amount = new()
        {
            Text = value,
            FontFamily = MonospaceFont,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(amount, 1);
        row.Children.Add(amount);
        return row;
    }

    private static Control CreateIcon(string name, IBrush? foreground = null, double size = 16)
    {
        string data = name switch
        {
            "wallet" => "M3,6 H21 V20 H3 Z M3,8 H21 M16,12 H21 V17 H16 A2,2 0 0 1 16,12 Z",
            "upload" => "M12,16 V4 M12,4 L7,9 M12,4 L17,9 M5,15 V20 H19 V15",
            "save" => "M5,4 H17 L20,7 V20 H5 Z M8,4 V10 H16 V4 M8,20 V14 H17",
            "search" => "M11,4 A7,7 0 1 0 11,18 A7,7 0 1 0 11,4 M16,16 L21,21",
            "card" => "M3,6 H21 V19 H3 Z M3,10 H21 M7,15 H11",
            "review" => "M5,4 H19 V20 H5 Z M8,8 H16 M8,12 H16 M8,16 H13",
            "arrow" => "M4,12 H20 M15,7 L20,12 L15,17",
            "check" => "M5,12 L10,17 L20,6",
            "history" => "M4,12 A8,8 0 1 0 6,7 M4,4 V9 H9 M12,7 V12 L16,14",
            "shield" => "M12,3 L20,6 V11 C20,16 17,19 12,21 C7,19 4,16 4,11 V6 Z M8,12 L11,15 L16,9",
            "close" => "M6,6 L18,18 M18,6 L6,18",
            "cart" => "M3,4 H5 L7,16 H18 L21,7 H6 M9,20 H9.1 M17,20 H17.1",
            "coffee" => "M5,8 H17 V15 A5,5 0 0 1 12,20 H10 A5,5 0 0 1 5,15 Z M17,10 H19 A2,2 0 0 1 19,14 H17",
            "train" => "M6,4 H18 V17 H6 Z M6,11 H18 M9,7 H9.1 M15,7 H15.1 M8,21 L10,17 M16,21 L14,17",
            "receipt" => "M6,3 H18 V21 L15,19 L12,21 L9,19 L6,21 Z M9,8 H15 M9,12 H15 M9,16 H13",
            _ => "M4,4 H20 V20 H4 Z",
        };
        Avalonia.Controls.Shapes.Path path = new()
        {
            Data = Geometry.Parse(data),
            Width = 24,
            Height = 24,
            Stroke = foreground ?? MutedBrush,
            StrokeThickness = 1.8,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent,
            Stretch = Stretch.Uniform,
        };
        return new Viewbox
        {
            Width = size,
            Height = size,
            Child = path,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static Control CreateBrandMark(double size)
    {
        Canvas canvas = new() { Width = 32, Height = 32 };
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M25,5.8 H10.2 A4.4,4.4 0 0 0 5.8,10.2 V21.8 A4.4,4.4 0 0 0 10.2,26.2 H25"),
            Fill = Brushes.Transparent,
            Stroke = AccentBrush,
            StrokeThickness = 3.2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        });
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M11,13 H22 M11,20 H22"),
            Fill = Brushes.Transparent,
            Stroke = AccentBrush,
            StrokeThickness = 3.2,
            StrokeLineCap = PenLineCap.Round,
        });
        Avalonia.Controls.Shapes.Ellipse dot = new()
        {
            Width = 7,
            Height = 7,
            Fill = SuccessBrush,
        };
        Canvas.SetLeft(dot, 21.5);
        Canvas.SetTop(dot, 2.3);
        canvas.Children.Add(dot);
        return new Viewbox
        {
            Width = size,
            Height = size,
            Child = canvas,
        };
    }

    private static string CategoryIcon(Transaction transaction) => transaction.Category switch
    {
        "Groceries" => "cart",
        "Dining" => "coffee",
        "Travel" => "train",
        _ => "receipt",
    };

    private static IBrush CategoryBrush(string category) => category switch
    {
        "Groceries" => SuccessBrush,
        "Dining" => AccentBrush,
        "Travel" => new SolidColorBrush(Color.Parse("#61D5E6")),
        _ => WarningBrush,
    };

    private static string TransactionName(Transaction transaction) =>
        FirstValue(transaction.CustomName, transaction.Counterparty, transaction.Merchant, transaction.AdditionalDetails, transaction.Type, transaction.Id);

    private static string TransactionNote(Transaction transaction) =>
        FirstValue(transaction.Notes, transaction.AdditionalDetails, transaction.Subcategory, transaction.Category, "Imported transaction");

    private static string FirstValue(params string[] values) => values.First(value => !string.IsNullOrWhiteSpace(value));

    private static string TransactionStatus(Transaction transaction) =>
        transaction.RequiresReview ? "Review" : transaction.FundingSource is null ? "Excluded" : "Ready";

    private static string DescribePreview(TransferPlan plan, FundingSource source)
    {
        SourceContribution joint = plan.Contributions.Single(contribution => contribution.Source == FundingSource.Joint);
        SourceContribution shared = plan.Contributions.Single(contribution => contribution.Source == FundingSource.SharedSavings);
        return $"Preview only ({SourceLabel(source)} rule not applied): Joint {Pounds(joint.Total)}, "
            + $"Shared Savings {Pounds(shared.Total)}, personal transfers {Pounds(plan.AdditionalPersonalTransfers)}.";
    }

    private static string SourceLabel(FundingSource? source) => source switch
    {
        FundingSource.Joint => "Joint",
        FundingSource.SharedSavings => "Shared Savings",
        FundingSource.Personal => "Personal",
        _ => "Review",
    };

    private static FundingSource ParseSource(string? value) => value switch
    {
        "Joint" => FundingSource.Joint,
        "Shared Savings" => FundingSource.SharedSavings,
        _ => FundingSource.Personal,
    };

    private static string Pounds(decimal amount) => amount.ToString("C2", BritishCulture);

    private static string SignedPounds(decimal amount) =>
        amount > 0 ? $"+{Pounds(amount)}" : amount < 0 ? $"−{Pounds(Math.Abs(amount))}" : Pounds(amount);
}
