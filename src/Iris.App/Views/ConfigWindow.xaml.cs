using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Iris.App.Services;
using Iris.App.ViewModels;
using Iris.Core.Config;
using Iris.Core.Settings;
using Iris.Core.Storage;
using Microsoft.Win32;

namespace Iris.App.Views;

/// <summary>
/// The Social Overlay config window: player, org and org-rule lists. Edits happen on row view models;
/// nothing is written until Save. Names are checked against RSI (cache first) when the user leaves the cell.
/// The Settings tab holds this PC's app settings, the shared-config buttons (import, export) and "Update database now".
/// One Save button saves the lists (if they changed) and the settings (if they changed).
/// Later increments add the overlay controls to this window.
/// </summary>
public partial class ConfigWindow : Window, IImportHost
{
    /// <summary>What a save writes.</summary>
    private enum SaveScope
    {
        /// <summary>The lists (if changed) and the settings (if changed), including the hosted address box. The Save button.</summary>
        ListsAndSettings,

        /// <summary>Like <see cref="ListsAndSettings"/>, but the hosted address and version stay as stored (import from a URL).</summary>
        ListsAndSettingsKeepHostedUrl,

        /// <summary>The lists only (export).</summary>
        ListsOnly,
    }

    private const string ConfigFileFilter = "IRIS config (*.toml)|*.toml|All files (*.*)|*.*";

    private readonly AppServices _services;
    private readonly ImportCoordinator _imports;
    private readonly DatabaseUpdater _updater;
    private readonly ObservableCollection<PlayerRowViewModel> _players = new();
    private readonly ObservableCollection<OrgRowViewModel> _orgs = new();
    private readonly ObservableCollection<RuleRowViewModel> _rules = new();
    private readonly CancellationTokenSource _closing = new();

    // Name checks that are running, per row. Save waits for these (a click on Save first makes the cell lose focus,
    // which starts a check; without waiting, Save would be refused with "still being checked").
    private readonly Dictionary<ListRowViewModel, Task> _checks = new();
    private bool _saving;
    private bool _invalidFileCopied;
    private bool _isClosed;

    // While an import, export or save-for-import runs the window cannot be closed and the import buttons are off.
    private int _busyCount;

    private string? _listName;
    private string _baseline = string.Empty;

    public ConfigWindow(AppServices services, ImportCoordinator imports, DatabaseUpdater updater)
    {
        _services = services;
        _imports = imports;
        _updater = updater;
        InitializeComponent();

        PlayersList.ItemsSource = _players;
        OrgsList.ItemsSource = _orgs;
        RulesList.ItemsSource = _rules;

        LoadRowsFrom(services.CurrentConfig);
        LoadSettingsControls();
        _baseline = Snapshot();

        imports.Host = this;
        updater.RunningChanged += OnUpdaterRunningChanged;
        UpdateNowButton.IsEnabled = !updater.IsRunning;

        Closed += (_, _) =>
        {
            _isClosed = true;
            _updater.RunningChanged -= OnUpdaterRunningChanged;
            if (ReferenceEquals(_imports.Host, this))
                _imports.Host = null;
            _closing.Cancel();
            _closing.Dispose();
        };
    }

    /// <summary>
    /// Replaces the rows on screen with those of <paramref name="config"/> and checks every name now:
    /// fresh cache entries answer at once, the rest go through the shared RSI limiter.
    /// </summary>
    private void LoadRowsFrom(SocialConfig config)
    {
        _listName = config.Name; // the shared list's name is kept as loaded
        _checks.Clear();
        _players.Clear();
        _orgs.Clear();
        _rules.Clear();

        _services.Exclusions.ReloadIfChanged();
        foreach (var p in config.Players)
            _players.Add(new PlayerRowViewModel(p.Handle, p.Color, IsExcluded));
        foreach (var o in config.Orgs)
            _orgs.Add(new OrgRowViewModel(o.Sid, o.Priority, o.Color));
        foreach (var r in config.OrgRules)
            _rules.Add(new RuleRowViewModel(r.Kind, r.Value, r.Priority, r.Color));

        foreach (var row in NameRows().ToList())
            StartCheck(row);
    }

    private bool IsExcluded(string handle) => _services.Exclusions.IsExcluded(handle);

    // ---------------------------------------------------------------------------------------
    // Adding and removing rows
    // ---------------------------------------------------------------------------------------

    private void OnAddPlayer(object sender, RoutedEventArgs e)
    {
        var row = new PlayerRowViewModel(string.Empty, OverlayColor.Cyan, IsExcluded);
        _players.Add(row);
        FocusNewRow(PlayersList, row);
    }

    private void OnAddOrg(object sender, RoutedEventArgs e)
    {
        var row = new OrgRowViewModel(string.Empty, Choices.DefaultPriority, OverlayColor.Red);
        _orgs.Add(row);
        FocusNewRow(OrgsList, row);
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        var row = new RuleRowViewModel(AttributeKind.Archetype, null, Choices.DefaultPriority, OverlayColor.Orange);
        _rules.Add(row);
        FocusNewRow(RulesList, row);
    }

    private void OnRemoveRow(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.DataContext)
        {
            case PlayerRowViewModel player:
                _players.Remove(player);
                RecheckDuplicates(_players);
                break;
            case OrgRowViewModel org:
                _orgs.Remove(org);
                RecheckDuplicates(_orgs);
                break;
            case RuleRowViewModel rule:
                _rules.Remove(rule);
                break;
        }
    }

    /// <summary>After a removal, rows that were flagged "already in the list" may be fine now: check them again.</summary>
    private void RecheckDuplicates(IEnumerable<ListRowViewModel> rows, ListRowViewModel? except = null)
    {
        foreach (var row in rows.Where(r => r.IsFlaggedDuplicate && !ReferenceEquals(r, except)).ToList())
        {
            row.ResetStatus();
            StartCheck(row);
        }
    }

    /// <summary>Scrolls the new row into view and puts the cursor in its first cell. The row's visuals exist a moment later.</summary>
    private void FocusNewRow(ItemsControl list, object item)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            list.UpdateLayout();
            if (list.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
                return;
            container.BringIntoView();
            FindFirst<Control>(container)?.Focus();
        }));
    }

    private static T? FindFirst<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            var deeper = FindFirst<T>(child);
            if (deeper is not null)
                return deeper;
        }
        return null;
    }

    // ---------------------------------------------------------------------------------------
    // Checking names against RSI
    // ---------------------------------------------------------------------------------------

    private void OnValueLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ListRowViewModel row)
            return;

        StartCheck(row);

        // The edit may have removed a clash: rows of the same list that were flagged "already in the list" get another look.
        RecheckDuplicates(row is OrgRowViewModel ? _orgs.Cast<ListRowViewModel>() : _players.Cast<ListRowViewModel>(), except: row);
    }

    /// <summary>
    /// Enter in a name box ends editing instead of pressing Save: the keyboard focus moves to an invisible element
    /// (a neutral target, so it does not jump into another row), which raises LostKeyboardFocus and starts the check.
    /// </summary>
    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        EndEditing();
    }

    /// <summary>
    /// Moves keyboard focus (and the window's remembered focus) to <c>FocusSink</c>. Focusing the window itself does not
    /// work: a Window is a focus scope and passes keyboard focus straight back to the element it remembers.
    /// </summary>
    private void EndEditing()
    {
        FocusManager.SetFocusedElement(this, FocusSink);
        Keyboard.Focus(FocusSink);
    }

    /// <summary>A click on empty space ends editing: a focused text box gives up focus (and so gets checked).</summary>
    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.FocusedElement is not TextBox)
            return;
        if (IsInteractive(e.OriginalSource as DependencyObject))
            return;
        EndEditing();
    }

    private static bool IsInteractive(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is TextBoxBase or ComboBox or ComboBoxItem or ButtonBase or ScrollBar or Thumb)
                return true;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    /// <summary>
    /// When the user picks Org-Archetype or Org-Activity as the Type, the Value list opens at once.
    /// The Value cell is rebuilt from a template when the type changes, so this waits until that has happened.
    /// </summary>
    private void OnRuleKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox kindBox || !kindBox.IsKeyboardFocusWithin || e.AddedItems.Count == 0)
            return; // not a user pick (the row is only being loaded)
        if (kindBox.DataContext is not RuleRowViewModel)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (kindBox.DataContext is not RuleRowViewModel { NeedsValue: true })
                return;
            if (VisualTreeHelper.GetParent(kindBox) is not Grid rowGrid)
                return;
            var cell = rowGrid.Children.OfType<ContentControl>().FirstOrDefault();
            if (cell is null)
                return;
            cell.UpdateLayout();
            var valueBox = FindFirst<ComboBox>(cell);
            if (valueBox is not null)
            {
                valueBox.Focus();
                valueBox.IsDropDownOpen = true;
            }
        }));
    }

    /// <summary>Starts a check and remembers its task so Save can wait for it. A finished or skipped check is not tracked.</summary>
    private void StartCheck(ListRowViewModel row)
    {
        var task = CheckRowAsync(row);
        if (task.IsCompleted)
            return;
        _checks[row] = task;
        _ = ForgetWhenDoneAsync(row, task);
    }

    /// <summary>Removes the task from <see cref="_checks"/> when it ends, unless a newer check for the same row replaced it.</summary>
    private async Task ForgetWhenDoneAsync(ListRowViewModel row, Task task)
    {
        try
        {
            await task; // continues on the window's own thread
        }
        catch (Exception)
        {
            // CheckRowAsync reports its own problems; nothing to do here
        }

        if (_checks.TryGetValue(row, out var current) && ReferenceEquals(current, task))
            _checks.Remove(row);
    }

    /// <summary>
    /// Checks one row without blocking the window. The work runs on a background thread; when it ends the code
    /// continues on the window's own thread (the Dispatcher), so changing the row there is safe.
    /// If the text was edited meanwhile (the row's Version moved on) the late result is dropped.
    /// </summary>
    private async Task CheckRowAsync(ListRowViewModel row)
    {
        var text = row.Value.Trim();
        if (text.Length == 0)
            return;
        if (row.StatusKind != RowStatusKind.Unchecked && row.StatusKind != RowStatusKind.Failed)
            return; // already has a result for this text (or is being checked)

        var isOrg = row is OrgRowViewModel;
        var others = (isOrg ? _orgs.Cast<ListRowViewModel>() : _players.Cast<ListRowViewModel>())
            .Where(r => !ReferenceEquals(r, row))
            .Select(r => r.Value)
            .ToList();

        var version = row.Version;
        var token = _closing.Token;
        row.BeginCheck();

        RowCheckResult result;
        try
        {
            result = isOrg
                ? await Task.Run(() => _services.Validator.ValidateOrgAsync(text, others, token))
                : await Task.Run(() => _services.Validator.ValidatePlayerAsync(text, others, token));
        }
        catch (OperationCanceledException)
        {
            return; // the window was closed
        }
        catch (Exception ex)
        {
            _services.Log.Write(DateTimeOffset.UtcNow, "Config check crashed: " + ex.Message);
            result = new RowCheckResult(RowCheckState.Failed, "Check failed: " + ex.Message);
        }

        if (row.Version != version || !IsInList(row))
            return; // edited again, or removed, while the check ran
        row.ApplyResult(result);
    }

    private bool IsInList(ListRowViewModel row) => row switch
    {
        PlayerRowViewModel player => _players.Contains(player),
        OrgRowViewModel org => _orgs.Contains(org),
        _ => false,
    };

    // ---------------------------------------------------------------------------------------
    // Save and Cancel
    // ---------------------------------------------------------------------------------------

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_saving || _busyCount > 0)
            return;
        _saving = true;
        try
        {
            if (await SaveAfterChecksAsync(SaveScope.ListsAndSettings, allowWhileBusy: false))
                Close(); // saved: the baseline was updated, so OnWindowClosing does not ask about unsaved changes
        }
        catch (Exception ex)
        {
            if (!_closing.IsCancellationRequested)
            {
                FooterStatus.Text = "Not saved.";
                MessageBox.Show(this, "Your changes could not be saved:\n\n" + ex.Message, "IRIS", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _saving = false;
        }
    }

    /// <summary>
    /// Waits for running name checks (up to 25 seconds), then saves what <paramref name="scope"/> says. True only when everything that changed was written.
    /// The Save button must not save while an import or export is running (<paramref name="allowWhileBusy"/> false);
    /// the import's and export's own "save first" is part of that busy work and passes true.
    /// </summary>
    private async Task<bool> SaveAfterChecksAsync(SaveScope scope, bool allowWhileBusy)
    {
        var pending = _checks.Values.Where(t => !t.IsCompleted).ToList();
        if (pending.Count > 0)
        {
            FooterStatus.Text = "Waiting for name checks…";
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromSeconds(25), _closing.Token));
            if (_isClosed || _closing.IsCancellationRequested)
                return false; // the window was closed meanwhile
            FooterStatus.Text = string.Empty;
        }

        // An import or export may have started while the checks were awaited: its work must not be saved over.
        if (!allowWhileBusy && _busyCount > 0)
        {
            FooterStatus.Text = "Not saved: an import or export is running. Try again when it is done.";
            return false;
        }

        return SaveCore(scope);
    }

    /// <summary>
    /// Saves what changed, as far as <paramref name="scope"/> goes: the lists (validated first) and the settings.
    /// An invalid hosted address blocks a save that includes it. Returns true when nothing is left unsaved.
    /// </summary>
    private bool SaveCore(SaveScope scope)
    {
        if (scope == SaveScope.ListsAndSettings && !ValidateHostedUrl())
        {
            Tabs.SelectedItem = SettingsTab;
            MessageBox.Show(
                this,
                "The hosted config URL is not valid:\n\n" + HostedUrlError.Text + "\n\nFix it or clear the box, then save again.",
                "IRIS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var listsSaved = false;
        if (Snapshot() != _baseline)
        {
            if (!SaveListsCore())
                return false;
            listsSaved = true;
        }

        if (scope != SaveScope.ListsOnly)
        {
            var settings = BuildSettings();
            if (scope == SaveScope.ListsAndSettingsKeepHostedUrl)
                settings = settings with { HostedConfigUrl = _services.Settings.HostedConfigUrl };

            // An unreadable settings.toml is rewritten by the first save even when no setting was edited.
            if (settings != _services.Settings || _services.SettingsFileNeedsSave)
            {
                var savedSettings = _services.SaveSettings(settings);
                if (!savedSettings.Success)
                {
                    MessageBox.Show(this, (savedSettings.Error ?? "The settings could not be saved.")
                        + (listsSaved ? "\n\nYour lists were saved, the settings were not." : "\n\nThe settings were not saved."),
                        "IRIS", MessageBoxButton.OK, MessageBoxImage.Error);
                    FooterStatus.Text = "Settings not saved.";
                    return false;
                }
            }
        }

        FooterStatus.Text = "Saved at " + DateTime.Now.ToString("HH:mm:ss") + ".";
        return true;
    }

    /// <summary>Validates and writes the list. Returns true only when the file was written.</summary>
    private bool SaveListsCore()
    {
        var problems = new List<string>();
        CollectNameProblems("Players", _players, RowValidator.CheckHandleFormat, problems);
        CollectNameProblems("Orgs", _orgs, RowValidator.CheckSidFormat, problems);
        for (int i = 0; i < _rules.Count; i++)
        {
            var rule = _rules[i];
            if (rule.NeedsValue && rule.Value.Trim().Length == 0)
                problems.Add($"Org-Attribute, row {i + 1}: the {Choices.DisplayName(rule.Kind)} rule needs a value.");
            else if (!rule.IsValueAcceptable)
                problems.Add($"Org-Attribute, row {i + 1}: \"{rule.Value}\" is not a known {Choices.DisplayName(rule.Kind)} value. Pick one from the list.");
        }

        if (problems.Count > 0)
        {
            MessageBox.Show(
                this,
                "These rows must be fixed before saving:\n\n" + string.Join("\n", problems.Take(15))
                + (problems.Count > 15 ? $"\n...and {problems.Count - 15} more." : string.Empty),
                "IRIS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var nameRows = NameRows().ToList();
        var checking = nameRows.Count(r => r.StatusKind == RowStatusKind.Checking);
        if (checking > 0)
        {
            MessageBox.Show(
                this,
                $"{Plural(checking, "name is", "names are")} still being checked. Wait a moment and press Save again.",
                "IRIS", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var notFound = nameRows.Count(r => r.StatusKind == RowStatusKind.NotFound);
        var failed = nameRows.Count(r => r.StatusKind == RowStatusKind.Failed);
        if (notFound > 0 || failed > 0)
        {
            var lines = new List<string>();
            if (notFound > 0)
                lines.Add($"{Plural(notFound, "name was", "names were")} not found on RSI.");
            if (failed > 0)
                lines.Add($"{Plural(failed, "name", "names")} could not be checked (RSI not reachable?).");
            var answer = MessageBox.Show(
                this, string.Join("\n", lines) + "\n\nSave anyway?",
                "IRIS", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                return false;
        }

        if (!KeepCopyOfInvalidFile())
            return false;

        var config = BuildConfig();
        var saved = SocialListStore.Save(_services.Paths.SocialListFile, config);
        if (!saved.Success)
        {
            MessageBox.Show(this, saved.Error ?? "The list could not be saved.", "IRIS", MessageBoxButton.OK, MessageBoxImage.Error);
            FooterStatus.Text = "Not saved.";
            return false;
        }

        _services.SetCurrentConfig(config);
        _baseline = Snapshot();
        return true;
    }

    /// <summary>
    /// The list file could not be read completely at startup, and the first save replaces it with what is on screen.
    /// Asks first, then keeps a copy of the original beside it. Returns false when the user said no or the copy failed.
    /// </summary>
    private bool KeepCopyOfInvalidFile()
    {
        var load = _services.ListLoad;
        if (load.IsValid || _invalidFileCopied)
            return true;

        var listFile = _services.Paths.SocialListFile;
        if (!File.Exists(listFile))
        {
            _invalidFileCopied = true;
            return true;
        }

        var copy = listFile + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var answer = MessageBox.Show(
            this,
            $"Your list file ({Path.GetFileName(listFile)}) had {Plural(load.Errors.Count, "problem", "problems")} when IRIS started.\n\n"
            + "Saving replaces the file with what you see in this window. Entries in the file that could not be read will be dropped.\n\n"
            + "A copy of the original file is kept as:\n" + Path.GetFileName(copy) + "\n\nSave now?",
            "IRIS", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return false;

        try
        {
            File.Copy(listFile, copy, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "A copy of the original file could not be made, so nothing was saved:\n\n" + ex.Message,
                "IRIS", MessageBoxButton.OK, MessageBoxImage.Error);
            FooterStatus.Text = "Not saved.";
            return false;
        }

        _invalidFileCopied = true;
        return true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Close(); // OnWindowClosing asks first if there are unsaved changes
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_busyCount > 0)
        {
            e.Cancel = true; // an import, export or save is in the middle of its work
            return;
        }
        if (!HasUnsavedChanges(ignoreHostedUrl: false))
            return;
        var answer = MessageBox.Show(
            this, "You have unsaved changes. Discard them?",
            "IRIS", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            e.Cancel = true;
    }

    /// <summary>The rows as a SocialConfig, in on-screen order (order is the tiebreak between equal priorities).</summary>
    private SocialConfig BuildConfig()
    {
        var config = new SocialConfig { Name = _listName };
        foreach (var p in _players)
            config.Players.Add(new PlayerEntry(p.Value.Trim(), p.Color));
        foreach (var o in _orgs)
            config.Orgs.Add(new OrgEntry(o.Value.Trim(), o.Priority, o.Color));
        foreach (var r in _rules)
            config.OrgRules.Add(new OrgAttributeRule(r.Kind, r.NeedsValue ? r.Value.Trim() : null, r.Priority, r.Color));
        return config;
    }

    /// <summary>A text form of everything on screen, to tell whether anything changed since the last save.</summary>
    private string Snapshot()
    {
        var lines = new List<string>();
        lines.AddRange(_players.Select(p => $"P|{p.Value.Trim()}|{p.Color}"));
        lines.AddRange(_orgs.Select(o => $"O|{o.Value.Trim()}|{o.Priority}|{o.Color}"));
        lines.AddRange(_rules.Select(r => $"R|{r.Kind}|{(r.NeedsValue ? r.Value.Trim() : string.Empty)}|{r.Priority}|{r.Color}"));
        return string.Join("\n", lines);
    }

    private IEnumerable<ListRowViewModel> NameRows()
    {
        foreach (var p in _players)
            yield return p;
        foreach (var o in _orgs)
            yield return o;
    }

    private static void CollectNameProblems(
        string tab, IEnumerable<ListRowViewModel> rows, Func<string?, RowCheckResult?> checkFormat, List<string> problems)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int number = 0;
        foreach (var row in rows)
        {
            number++;
            var text = row.Value.Trim();
            if (text.Length == 0)
            {
                problems.Add($"{tab}, row {number}: empty (fill it in or remove the row).");
                continue;
            }

            var bad = checkFormat(text);
            if (bad is not null)
                problems.Add($"{tab}, row {number}: \"{text}\" - {bad.Message}.");
            else if (!seen.Add(text))
                problems.Add($"{tab}, row {number}: \"{text}\" is already in the list.");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Settings tab
    // ---------------------------------------------------------------------------------------

    /// <summary>Shows the saved settings in the controls (also used to throw away edits).</summary>
    private void LoadSettingsControls()
    {
        var settings = _services.Settings;
        CacheTtlCombo.SelectedIndex = settings.CacheTtlDays == 7 ? 1 : 0;
        LogEnabledCheck.IsChecked = settings.SessionLogEnabled;
        CheckUpdatesCheck.IsChecked = settings.CheckForUpdates;
        HostedUrlBox.Text = settings.HostedConfigUrl;
        HostedUrlError.Text = string.Empty;
        HostedUrlError.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// The settings as the controls show them. The hosted file's hash is not on screen, so it always comes from the saved settings
    /// (an import or an "Update database now" can change it while this window is open).
    /// </summary>
    private AppSettings BuildSettings() => _services.Settings with
    {
        CacheTtlDays = CacheTtlCombo.SelectedIndex == 1 ? 7 : 30,
        SessionLogEnabled = LogEnabledCheck.IsChecked == true,
        CheckForUpdates = CheckUpdatesCheck.IsChecked == true,
        HostedConfigUrl = HostedUrlBox.Text.Trim(),
    };

    /// <summary>Checks the hosted address box (empty is fine) and shows or hides the red message under it. True when it is acceptable.</summary>
    private bool ValidateHostedUrl()
    {
        var text = HostedUrlBox.Text.Trim();
        var problem = text.Length == 0 ? null : HostedConfigUrl.Check(text);
        HostedUrlError.Text = problem ?? string.Empty;
        HostedUrlError.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        return problem is null;
    }

    private void OnHostedUrlLostFocus(object sender, KeyboardFocusChangedEventArgs e) => ValidateHostedUrl();

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = _services.Paths.LogsFolder;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FooterStatus.Text = "Could not open the log folder: " + ex.Message;
        }
    }

    private async void OnUpdateNow(object sender, RoutedEventArgs e)
    {
        if (_updater.IsRunning)
            return;

        FooterStatus.ToolTip = null;
        FooterStatus.Text = "Updating the database… (one request every 3 seconds)";
        var message = await _updater.RunAsync(); // greys the button out through RunningChanged; null when IRIS is quitting
        if (message is not null && !_isClosed)
        {
            FooterStatus.Text = message;
            FooterStatus.ToolTip = message; // the status bar trims long text
        }
    }

    private void OnUpdaterRunningChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnUpdaterRunningChanged(sender, e)));
            return;
        }
        UpdateNowButton.IsEnabled = !_updater.IsRunning;
    }

    // ---------------------------------------------------------------------------------------
    // Import and export
    // ---------------------------------------------------------------------------------------

    private async void OnImportFromFile(object sender, RoutedEventArgs e)
    {
        if (_busyCount > 0)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Import IRIS config",
            Filter = ConfigFileFilter,
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var read = ConfigImporter.ReadFile(dialog.FileName);
        if (!read.Success || read.Text is null)
        {
            FooterStatus.Text = "Import failed.";
            MessageBox.Show(this, "The file could not be imported:\n\n" + read.Error, "IRIS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BeginBusy();
        try
        {
            var attempt = await _imports.ImportTextAsync(read.Text, "Import " + Path.GetFileName(dialog.FileName), showDeclineNote: false, ignoreHostedUrl: false);
            if (!_isClosed && attempt.Outcome != ImportOutcome.Imported)
                FooterStatus.Text = attempt.Message; // on success the reload has already shown the summary
        }
        catch (Exception ex)
        {
            _services.Log.Write(DateTimeOffset.UtcNow, "Import from file failed: " + ex);
            if (!_isClosed)
                FooterStatus.Text = "Import failed: " + ex.Message;
        }
        finally
        {
            EndBusy();
        }
    }

    private async void OnImportFromUrl(object sender, RoutedEventArgs e)
    {
        if (_busyCount > 0)
            return;

        var url = HostedUrlBox.Text.Trim();
        if (url.Length == 0)
        {
            HostedUrlError.Text = "Enter the web address of your config file first (it must start with https://).";
            HostedUrlError.Visibility = Visibility.Visible;
            HostedUrlBox.Focus();
            return;
        }
        if (!ValidateHostedUrl())
        {
            HostedUrlBox.Focus();
            return;
        }

        BeginBusy();
        try
        {
            FooterStatus.ToolTip = null;
            FooterStatus.Text = "Fetching " + url + "…";
            var fetched = await _services.FetchHostedConfigAsync(url);
            if (!fetched.Success || fetched.Text is null || fetched.Hash is null)
            {
                FooterStatus.Text = "Import failed.";
                MessageBox.Show(this, "The config could not be fetched:\n\n" + fetched.Error, "IRIS", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var hash = fetched.Hash;
            var attempt = await _imports.ImportTextAsync(fetched.Text, "Import from " + new Uri(url).Host, showDeclineNote: false, ignoreHostedUrl: true);
            if (attempt.Outcome != ImportOutcome.Imported)
            {
                FooterStatus.Text = attempt.Message;
                return;
            }

            // Remember the address and this version, so "Update database now" only asks again when the file changes.
            var remembered = _services.UpdateSettings(s => s with { HostedConfigUrl = url, HostedConfigLastHash = hash });
            LoadSettingsControls();
            FooterStatus.Text = remembered.Success
                ? attempt.Message
                : attempt.Message + " (the address could not be saved: " + remembered.Error + ")";
        }
        catch (OperationCanceledException)
        {
            // IRIS is quitting
        }
        catch (Exception ex)
        {
            _services.Log.Write(DateTimeOffset.UtcNow, "Import from URL failed: " + ex);
            if (!_isClosed)
                FooterStatus.Text = "Import failed: " + ex.Message;
        }
        finally
        {
            EndBusy();
        }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_busyCount > 0)
            return;

        BeginBusy();
        try
        {
            // The file holds the SAVED lists, so unsaved edits need a decision first.
            if (Snapshot() != _baseline)
            {
                var answer = MessageBox.Show(
                    this,
                    "You have unsaved changes to your lists.\n\nYes: save the lists, then export.\nNo: export the last saved version.\nCancel: stop.",
                    "IRIS", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
                if (answer == MessageBoxResult.Cancel)
                    return;
                if (answer == MessageBoxResult.Yes && !await SaveWithGuardAsync(SaveScope.ListsOnly))
                    return; // only the lists are saved here; other edits in the Settings tab stay unsaved
            }

            var dialog = new SaveFileDialog
            {
                Title = "Export IRIS config",
                Filter = ConfigFileFilter,
                FileName = "iris-config.toml",
                DefaultExt = ".toml",
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(this) != true)
                return;

            var result = ConfigImporter.Export(dialog.FileName, _services.CurrentConfig);
            if (result.Success)
            {
                FooterStatus.ToolTip = null;
                FooterStatus.Text = "Exported to " + Path.GetFileName(dialog.FileName);
            }
            else
            {
                FooterStatus.Text = "Export failed.";
                MessageBox.Show(this, result.Error ?? "The file could not be written.", "IRIS", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            _services.Log.Write(DateTimeOffset.UtcNow, "Export failed: " + ex);
            if (!_isClosed)
                FooterStatus.Text = "Export failed: " + ex.Message;
        }
        finally
        {
            EndBusy();
        }
    }

    // ---------------------------------------------------------------------------------------
    // IImportHost: what the import flow asks of this window
    // ---------------------------------------------------------------------------------------

    public Window DialogOwner => this;

    /// <summary>True while an import, export or save-for-import is running: the window cannot be closed then.</summary>
    public bool IsBusy => _busyCount > 0;

    /// <summary>Lists differ from the last save, or the settings controls differ from the saved settings.</summary>
    public bool HasUnsavedChanges(bool ignoreHostedUrl)
    {
        if (Snapshot() != _baseline)
            return true;

        var shown = BuildSettings();
        var saved = _services.Settings;
        if (ignoreHostedUrl)
            shown = shown with { HostedConfigUrl = saved.HostedConfigUrl };
        return shown != saved;
    }

    /// <summary>
    /// Saves lists and settings like the Save button does, but leaves the window open. With <paramref name="ignoreHostedUrl"/>
    /// the stored hosted address and version are kept (the import stores the address itself, and only after it worked).
    /// </summary>
    public Task<bool> SaveChangesAsync(bool ignoreHostedUrl) =>
        SaveWithGuardAsync(ignoreHostedUrl ? SaveScope.ListsAndSettingsKeepHostedUrl : SaveScope.ListsAndSettings);

    /// <summary>The save used by import and export: part of their busy work, one save at a time, problems reported in a message box.</summary>
    private async Task<bool> SaveWithGuardAsync(SaveScope scope)
    {
        if (_saving)
            return false;
        _saving = true;
        try
        {
            return await SaveAfterChecksAsync(scope, allowWhileBusy: true);
        }
        catch (Exception ex)
        {
            if (!_isClosed)
            {
                FooterStatus.Text = "Not saved.";
                MessageBox.Show(this, "Your changes could not be saved:\n\n" + ex.Message, "IRIS", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return false;
        }
        finally
        {
            _saving = false;
        }
    }

    public void BeginBusy()
    {
        _busyCount++;
        ApplyBusy();
    }

    public void EndBusy()
    {
        if (_busyCount > 0)
            _busyCount--;
        ApplyBusy();
    }

    private void ApplyBusy()
    {
        var idle = _busyCount == 0;
        ImportFileButton.IsEnabled = idle;
        ImportUrlButton.IsEnabled = idle;
        ExportButton.IsEnabled = idle;
    }

    /// <summary>The app's lists were replaced by an import: show them, check every name, show the settings as saved, and start clean.</summary>
    public void ReloadAfterImport(string status)
    {
        if (_isClosed)
            return;

        LoadRowsFrom(_services.CurrentConfig);
        LoadSettingsControls();
        _baseline = Snapshot();
        FooterStatus.ToolTip = null;
        FooterStatus.Text = status;
    }

    private static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";
}
