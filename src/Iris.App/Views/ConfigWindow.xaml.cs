using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
/// The Settings tab holds this PC's app settings, the shared-config buttons (import, export) and "Refresh Cache (long time)".
/// One Save button saves the lists (if they changed) and the settings (if they changed); it is greyed out while nothing differs from the
/// saved version, and saving does not close the window (the Close button does). While a watched file on disk has errors, Save stays
/// enabled on purpose, so the user can replace the broken file with what the window shows (a copy of the broken file is kept first).
/// While the window is open it looks at the config files every 2 seconds: an edited file is loaded (unsaved edits in the window are discarded),
/// a file with errors is reported in a red banner at the top and the previous version stays in use.
/// The Settings tab also has the Overlay section (right panel layout) and the "Show test overlay (15 s)" button.
/// </summary>
public partial class ConfigWindow : Window, IImportHost
{
    /// <summary>What a save writes.</summary>
    private enum SaveScope
    {
        /// <summary>The lists (if changed) and the settings (if changed), including the hosted address box. The Save button.</summary>
        ListsAndSettings,

        /// <summary>Like <see cref="ListsAndSettings"/>, but the hosted address, its 24-hour check and its version stay as stored (import from a URL).</summary>
        ListsAndSettingsKeepHostedUrl,

        /// <summary>The lists only (export).</summary>
        ListsOnly,
    }

    // Tag of the Value cell in the Org-Attribute row template (see ConfigWindow.xaml).
    private const string RuleValueCellTag = "RuleValueCell";

    private const string ConfigFileFilter = "IRIS config (*.toml)|*.toml|All files (*.*)|*.*";

    private readonly AppServices _services;
    private readonly ImportCoordinator _imports;
    private readonly CacheUpdater _updater;
    private readonly OverlayController _overlay;
    private readonly ObservableCollection<PlayerRowViewModel> _players = new();
    private readonly ObservableCollection<OrgRowViewModel> _orgs = new();
    private readonly ObservableCollection<RuleRowViewModel> _rules = new();
    private readonly CancellationTokenSource _closing = new();

    // Name checks that are running, per row. Save waits for these (a click on Save first makes the cell lose focus,
    // which starts a check; without waiting, Save would be refused with "still being checked").
    private readonly Dictionary<ListRowViewModel, Task> _checks = new();
    private bool _saving;
    private bool _isClosed;

    // The panel switch as the controls last knew it from the saved settings (see OnServicesSettingsApplied).
    private bool _knownPanelEnabled = true;

    // True while the code (not the user) sets the "Disable Overlay" check box, so that its change event does not toggle the setting again.
    private bool _syncingOverlayDisabled;

    // True from the moment the user stops the test overlay with the button until the state-changed event for it was handled
    // (the footer then says "stopped", not "ended").
    private bool _testStoppedByUser;

    // Set once the window is built: the control events fire while it is still being created and must not touch anything yet.
    private bool _ready;

    // The bad version of the list file that was already copied to ".invalid-<time>" (so the copy is made once per bad version).
    private string? _copiedInvalidKey;

    // Watching the config files (see OnWatchTick).
    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _polling;
    private bool _closePromptOpen;
    private ConfigPoll? _pendingPoll;

    // While an import, export or save-for-import runs the window cannot be closed and the import buttons are off.
    private int _busyCount;

    private string? _listName;
    private string _baseline = string.Empty;

    private static readonly string MissingListNotice = ConfigMessages.ListFileMissing(DataPaths.SocialListFileName);

    // The bad version of the settings file that was already copied to ".invalid-<time>" (see _copiedInvalidKey).
    private string? _copiedInvalidSettingsKey;

    public ConfigWindow(AppServices services, ImportCoordinator imports, CacheUpdater updater, OverlayController overlay)
    {
        _services = services;
        _imports = imports;
        _updater = updater;
        _overlay = overlay;
        InitializeComponent();

        // The slider ranges come from the settings limits (one place). The handlers ignore changes until the window is built (_ready).
        ConfigureSlider(PanelWidthSlider, AppSettings.PanelWidthPercentMin, AppSettings.PanelWidthPercentMax);
        ConfigureSlider(PanelTextSizeSlider, AppSettings.PanelTextSizeMin, AppSettings.PanelTextSizeMax);
        ConfigureSlider(PanelRightGapSlider, AppSettings.PanelRightGapPercentMin, AppSettings.PanelRightGapPercentMax);
        ConfigureSlider(PanelTopGapSlider, AppSettings.PanelTopGapPercentMin, AppSettings.PanelTopGapPercentMax);

        PlayersList.ItemsSource = _players;
        OrgsList.ItemsSource = _orgs;
        RulesList.ItemsSource = _rules;

        // Any change to the rows (added, removed, moved, or a value edited) recomputes whether Save is needed.
        _players.CollectionChanged += OnRowsChanged;
        _orgs.CollectionChanged += OnRowsChanged;
        _rules.CollectionChanged += OnRowsChanged;

        LoadRowsFrom(services.CurrentConfig);
        LoadSettingsControls();
        _baseline = Snapshot();

        imports.Host = this;
        updater.RefreshingChanged += OnRefreshingChanged;
        RefreshCacheButton.IsEnabled = !updater.IsRefreshing;
        overlay.TestStateChanged += OnTestStateChanged;
        overlay.Hotkeys.StatusChanged += OnHotkeyStatusChanged;
        services.SettingsApplied += OnServicesSettingsApplied;
        UpdateTestButton();

        _watchTimer.Tick += OnWatchTick;
        Loaded += (_, _) =>
        {
            _watchTimer.Start();
            OnWatchTick(this, EventArgs.Empty); // look at the files right away (on a background thread): they may have been edited while the window was closed
        };

        Closed += (_, _) =>
        {
            _isClosed = true;
            _watchTimer.Stop();
            _watchTimer.Tick -= OnWatchTick;
            _updater.RefreshingChanged -= OnRefreshingChanged;
            _overlay.TestStateChanged -= OnTestStateChanged;
            _overlay.Hotkeys.StatusChanged -= OnHotkeyStatusChanged;
            _services.SettingsApplied -= OnServicesSettingsApplied;
            if (ReferenceEquals(_imports.Host, this))
                _imports.Host = null;
            _closing.Cancel();
            _closing.Dispose();

            // A check result that was parked (a save or import was going on) was already recorded by the change detector as the version
            // IRIS knows: nobody would report it again, so apply it now. The window is gone; the tray balloon tells the user.
            if (_pendingPoll is { } parked)
            {
                _pendingPoll = null;
                try
                {
                    _services.ApplyPoll(parked);
                }
                catch (Exception ex)
                {
                    _services.Log.Write(DateTimeOffset.UtcNow, "Applying the last config check failed: " + ex);
                }
            }
        };

        _ready = true;
        RefreshStateUi();
        if (_services.ListFileMissing)
            SetFooter(MissingListNotice);
    }

    /// <summary>
    /// Replaces the rows on screen with those of <paramref name="config"/> and checks every name now:
    /// fresh cache entries answer at once, the rest go through the shared RSI limiter.
    /// </summary>
    private void LoadRowsFrom(SocialConfig config)
    {
        _listName = config.Name; // the shared list's name is kept as loaded
        _checks.Clear();

        // Clear() reports no removed items, so the rows must be let go of here, or their events would keep reaching this window.
        // (The exclusion list is not reloaded here: the next file check does that and reports it, see ShowConfigChanges.)
        foreach (var row in _players.Cast<INotifyPropertyChanged>()
                     .Concat<INotifyPropertyChanged>(_orgs).Concat<INotifyPropertyChanged>(_rules).ToList())
            row.PropertyChanged -= OnRowPropertyChanged;
        _players.Clear();
        _orgs.Clear();
        _rules.Clear();

        foreach (var p in config.Players)
            _players.Add(new PlayerRowViewModel(p.Handle, p.Color, IsExcluded));
        foreach (var o in config.Orgs)
            _orgs.Add(new OrgRowViewModel(o.Sid, o.Priority, o.Color));
        foreach (var r in config.OrgRules.Where(rule => rule.Kind != AttributeKind.NoMatch))
            _rules.Add(new RuleRowViewModel(r.Kind, r.Value, r.Priority, r.Color));

        // The permanent No Match row is always there, always last (a list without one gets the default).
        var noMatch = NoMatchRule.Find(config) ?? NoMatchRule.CreateDefault();
        _rules.Add(new RuleRowViewModel(AttributeKind.NoMatch, null, NoMatchRule.Priority, noMatch.Color));

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
        // New rules go ABOVE the permanent No Match row, which stays last.
        var row = new RuleRowViewModel(AttributeKind.Archetype, null, Choices.DefaultPriority, OverlayColor.Orange);
        var noMatchIndex = _rules.Count;
        while (noMatchIndex > 0 && _rules[noMatchIndex - 1].IsNoMatch)
            noMatchIndex--;
        _rules.Insert(noMatchIndex, row);
        FocusNewRow<ComboBox>(RulesList, row);
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
            case RuleRowViewModel rule when !rule.IsNoMatch: // the No Match row cannot be removed (its button is hidden, too)
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
    private void FocusNewRow(ItemsControl list, object item) => FocusNewRow<Control>(list, item);

    /// <summary>Same, putting the cursor in the first control of type <typeparamref name="T"/> of the row.</summary>
    private void FocusNewRow<T>(ItemsControl list, object item) where T : Control
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            list.UpdateLayout();
            if (list.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
                return;
            container.BringIntoView();
            FindFirst<T>(container)?.Focus();
        }));
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(start);
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
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
            var rowGrid = FindAncestor<Grid>(kindBox); // the Type drop-down sits in a cell (ContentControl) of the row's grid
            if (rowGrid is null)
                return;
            var cell = rowGrid.Children.OfType<ContentControl>().FirstOrDefault(c => Equals(c.Tag, RuleValueCellTag));
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
            // Saved: the baseline was updated and Save greys out again. The window stays open (Close closes it).
            await SaveAfterChecksAsync(SaveScope.ListsAndSettings, allowWhileBusy: false);
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
        var settingsSaved = false;
        var keptBroken = new List<string>(); // broken files the user chose not to replace (nothing the user edited was lost by that)

        var listsEdited = Snapshot() != _baseline;
        if (listsEdited || _services.ListFileHasErrors)
        {
            if (SaveListsCore())
                listsSaved = true;
            else if (listsEdited)
                return false; // real list edits did not get saved
            else
                keptBroken.Add(DataPaths.SocialListFileName); // only the broken file was to be replaced, and the user said no (or it failed)
        }

        if (scope != SaveScope.ListsOnly)
        {
            var settings = BuildSettings();
            if (scope == SaveScope.ListsAndSettingsKeepHostedUrl)
                settings = settings with
                {
                    HostedConfigUrl = _services.Settings.HostedConfigUrl,
                    HostedConfigAutoCheck = _services.Settings.HostedConfigAutoCheck,
                };

            // Settings are written only when they differ from the stored ones, or to replace a settings.toml that has errors
            // (then the user is asked first and a copy of the broken file is kept).
            var settingsEdited = settings != _services.Settings;
            if (settingsEdited || _services.SettingsFileNeedsSave)
            {
                if (KeepCopyOfInvalidSettingsFile() != CopyOutcome.Proceed)
                {
                    if (settingsEdited)
                    {
                        FooterStatus.Text = "Settings not saved.";
                        return false;
                    }
                    keptBroken.Add(DataPaths.SettingsFileName);
                }
                else
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
                    settingsSaved = true;
                }
            }
        }

        var stamp = "Saved at " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (keptBroken.Count == 0)
            SetFooter(stamp);
        else
            SetFooter((listsSaved || settingsSaved ? stamp + ". " : "Nothing saved. ")
                      + "Not replaced (still has errors): " + string.Join(", ", keptBroken) + ".");
        RefreshStateUi();
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

        if (KeepCopyOfInvalidListFile() != CopyOutcome.Proceed)
            return false;

        var config = BuildConfig();
        var saved = SocialListStore.Save(_services.Paths.SocialListFile, config, _services.ListFile); // the watched file: this save is not an outside edit
        if (!saved.Success)
        {
            MessageBox.Show(this, saved.Error ?? "The list could not be saved.", "IRIS", MessageBoxButton.OK, MessageBoxImage.Error);
            FooterStatus.Text = "Not saved.";
            return false;
        }

        _services.SetCurrentConfig(config);
        _baseline = Snapshot();
        RefreshStateUi();
        return true;
    }

    /// <summary>What <see cref="KeepCopyOfInvalidFile"/> came to.</summary>
    private enum CopyOutcome
    {
        /// <summary>The file is fine, a copy of it is already kept, or a copy was just made: go ahead and write.</summary>
        Proceed,

        /// <summary>The user said no to replacing the broken file.</summary>
        Declined,

        /// <summary>The copy could not be made (the user was told); nothing may be written.</summary>
        Failed,
    }

    /// <summary>
    /// The list file on disk has errors (at startup, or after an outside edit), and the first save replaces it with what is on screen.
    /// Asks first, then keeps a copy of the broken file beside it ("name.toml.invalid-yyyyMMdd-HHmmss"). Asked once per bad version of the file.
    /// </summary>
    private CopyOutcome KeepCopyOfInvalidListFile()
    {
        if (!_services.ListFileHasErrors)
            return CopyOutcome.Proceed;

        return KeepCopyOfInvalidFile(
            _services.Paths.SocialListFile, _services.ListFileErrorHash ?? "unreadable", ref _copiedInvalidKey,
            copyName =>
                $"Your list file ({DataPaths.SocialListFileName}) has {Plural(_services.ListFileErrors.Count, "problem", "problems")}.\n\n"
                + "Saving replaces the file with what you see in this window. Entries in the file that could not be read will be dropped.\n\n"
                + "A copy of the broken file is kept as:\n" + copyName + "\n\nSave now?");
    }

    /// <summary>Same for settings.toml, which has errors (at startup, or after an outside edit) and is about to be replaced.</summary>
    private CopyOutcome KeepCopyOfInvalidSettingsFile()
    {
        if (!_services.SettingsFileHasErrors)
            return CopyOutcome.Proceed;

        return KeepCopyOfInvalidFile(
            _services.Paths.SettingsFile, _services.SettingsFileErrorHash ?? "unreadable", ref _copiedInvalidSettingsKey,
            copyName =>
                $"{DataPaths.SettingsFileName} has errors. Replace it with the settings shown here? A copy of the broken file is kept.\n\n"
                + "The copy: " + copyName);
    }

    private CopyOutcome KeepCopyOfInvalidFile(string file, string key, ref string? copiedKey, Func<string, string> question)
    {
        if (copiedKey == key)
            return CopyOutcome.Proceed;

        if (!File.Exists(file))
        {
            copiedKey = key;
            return CopyOutcome.Proceed;
        }

        var copy = file + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var answer = MessageBox.Show(
            this, question(Path.GetFileName(copy)),
            "IRIS", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return CopyOutcome.Declined;

        try
        {
            File.Copy(file, copy, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "A copy of the original file could not be made, so nothing was saved:\n\n" + ex.Message,
                "IRIS", MessageBoxButton.OK, MessageBoxImage.Error);
            FooterStatus.Text = "Not saved.";
            return CopyOutcome.Failed;
        }

        copiedKey = key;
        return CopyOutcome.Proceed;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close(); // OnWindowClosing asks first if there are unsaved changes (the Escape key lands here too: the button is the cancel button)
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
        _closePromptOpen = true; // no file check (and no reload of the rows) while this question is open
        try
        {
            var answer = MessageBox.Show(
                this, "You have unsaved changes. Discard them?",
                "IRIS", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                e.Cancel = true;
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    /// <summary>The rows as a SocialConfig, in on-screen order (order is the tiebreak between equal priorities).</summary>
    private SocialConfig BuildConfig()
    {
        var config = new SocialConfig { Name = _listName };
        foreach (var p in _players)
            config.Players.Add(new PlayerEntry(p.Value.Trim(), p.Color));
        foreach (var o in _orgs)
            config.Orgs.Add(new OrgEntry(o.Value.Trim(), o.Priority, o.Color));
        // The No Match row is the last row; written last explicitly so the order can never be broken.
        foreach (var r in _rules.Where(rule => !rule.IsNoMatch))
            config.OrgRules.Add(new OrgAttributeRule(r.Kind, r.NeedsValue ? r.Value.Trim() : null, r.Priority, r.Color));
        var noMatch = _rules.FirstOrDefault(rule => rule.IsNoMatch);
        config.OrgRules.Add(new OrgAttributeRule(
            AttributeKind.NoMatch, null, NoMatchRule.Priority, noMatch?.Color ?? NoMatchRule.DefaultColor));
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
        _knownPanelEnabled = settings.PanelEnabled;
        PanelDisabledCheck.IsChecked = !settings.PanelEnabled;
        ShowOverlayDisabled(settings.OverlayDisabled);
        OverlayHotkeyBox.HotkeyValue = settings.HotkeyToggleOverlay;
        PanelHotkeyBox.HotkeyValue = settings.HotkeyTogglePanel;
        PanelWidthSlider.Value = settings.PanelWidthPercent;
        PanelTextSizeSlider.Value = settings.PanelTextSize;
        PanelRightGapSlider.Value = settings.PanelRightGapPercent;
        PanelTopGapSlider.Value = settings.PanelTopGapPercent;
        LogEnabledCheck.IsChecked = settings.SessionLogEnabled;
        CheckUpdatesCheck.IsChecked = settings.CheckForUpdates;
        HostedUrlBox.Text = settings.HostedConfigUrl;
        HostedUrlError.Text = string.Empty;
        HostedUrlError.Visibility = Visibility.Collapsed;
        HostedAutoCheck.IsChecked = settings.HostedConfigAutoCheck && IsHostedUrlUsable();
        UpdateAutoCheckState();
        UpdateHotkeyErrors();
        UpdateSaveButton();
    }

    /// <summary>
    /// The settings as the controls show them. The hosted file's hash is not on screen, so it always comes from the saved settings
    /// (an import, or a settings file edited from outside, can change it while this window is open).
    /// </summary>
    private AppSettings BuildSettings() => _services.Settings with
    {
        CacheTtlDays = CacheTtlCombo.SelectedIndex == 1 ? 7 : 30,
        PanelEnabled = PanelDisabledCheck.IsChecked != true,
        HotkeyToggleOverlay = OverlayHotkeyBox.HotkeyValue,
        HotkeyTogglePanel = PanelHotkeyBox.HotkeyValue,
        PanelWidthPercent = (int)Math.Round(PanelWidthSlider.Value),
        PanelTextSize = (int)Math.Round(PanelTextSizeSlider.Value),
        PanelRightGapPercent = (int)Math.Round(PanelRightGapSlider.Value),
        PanelTopGapPercent = (int)Math.Round(PanelTopGapSlider.Value),
        SessionLogEnabled = LogEnabledCheck.IsChecked == true,
        CheckForUpdates = CheckUpdatesCheck.IsChecked == true,
        HostedConfigUrl = HostedUrlBox.Text.Trim(),
        HostedConfigAutoCheck = HostedAutoCheck.IsChecked == true && IsHostedUrlUsable(),
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

    // ---- Hotkeys section ----

    private void OnHotkeyBoxChanged(object? sender, EventArgs e)
    {
        if (!_ready)
            return;
        UpdateHotkeyErrors();
        UpdateSaveButton();
    }

    private void OnClearOverlayHotkey(object sender, RoutedEventArgs e) => OverlayHotkeyBox.Clear();

    private void OnClearPanelHotkey(object sender, RoutedEventArgs e) => PanelHotkeyBox.Clear();

    private void OnHotkeyStatusChanged(object? sender, EventArgs e) => UpdateHotkeyErrors();

    /// <summary>
    /// Shows in red, next to a box, that its hotkey does not work because both boxes hold the same combination. That is checked on what is on
    /// screen; as long as a box still holds the saved hotkey the hotkey manager's own verdict (<c>OverlayError</c> / <c>PanelError</c>, also only
    /// the same-combination case) is shown too. IRIS does not register hotkeys with Windows, so there is no other kind of refusal.
    /// </summary>
    private void UpdateHotkeyErrors()
    {
        if (OverlayHotkeyError is null || PanelHotkeyError is null)
            return; // still being built
        var saved = _services.Settings;
        var overlayText = OverlayHotkeyBox.HotkeyValue;
        var panelText = PanelHotkeyBox.HotkeyValue;
        var same = overlayText.Length > 0 && overlayText == panelText;
        const string SameText = "Both hotkeys are the same. Choose two different ones.";

        ShowHotkeyError(OverlayHotkeyError,
            same ? SameText : overlayText == saved.HotkeyToggleOverlay ? _overlay.Hotkeys.OverlayError : null);
        ShowHotkeyError(PanelHotkeyError,
            same ? SameText : panelText == saved.HotkeyTogglePanel ? _overlay.Hotkeys.PanelError : null);
    }

    private static void ShowHotkeyError(TextBlock block, string? message)
    {
        block.Text = message ?? string.Empty;
        block.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// The saved settings changed (a tray click or a hotkey flipped a switch, or this window saved). If the panel switch differs from what
    /// the checkbox last knew, the checkbox follows it; edits of all other controls stay as they are, so a tray or hotkey toggle never discards
    /// unsaved edits. Because the saved value changed too, the window does not count that switch as an unsaved change.
    /// The "Disable Overlay" checkbox always shows the saved value (it acts at once and is not an edit).
    /// </summary>
    private void OnServicesSettingsApplied(object? sender, EventArgs e)
    {
        if (_isClosed || !_ready)
            return;
        var enabled = _services.Settings.PanelEnabled;
        if (enabled != _knownPanelEnabled)
        {
            _knownPanelEnabled = enabled;
            PanelDisabledCheck.IsChecked = !enabled;
        }
        ShowOverlayDisabled(_services.Settings.OverlayDisabled);
        UpdateHotkeyErrors();
        UpdateSaveButton();
    }

    // ---- Overlay section ----

    private static void ConfigureSlider(Slider slider, int minimum, int maximum)
    {
        slider.Maximum = maximum; // the maximum first: the default maximum (10) must not cap a larger minimum
        slider.Minimum = minimum;
    }

    /// <summary>Sets the "Disable Overlay" check box from the saved setting without toggling anything.</summary>
    private void ShowOverlayDisabled(bool disabled)
    {
        if (OverlayDisabledCheck.IsChecked == disabled)
            return;
        _syncingOverlayDisabled = true;
        try
        {
            OverlayDisabledCheck.IsChecked = disabled;
        }
        finally
        {
            _syncingOverlayDisabled = false;
        }
    }

    /// <summary>
    /// The user ticked or cleared "Disable Overlay": it acts at once, like the tray item (it saves just that setting and is no unsaved change of
    /// this tab). Afterwards the box shows the saved value again, which also puts it back if saving failed (the user was told through a balloon).
    /// </summary>
    private void OnOverlayDisabledCheckChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _isClosed || _syncingOverlayDisabled)
            return;
        var wanted = OverlayDisabledCheck.IsChecked == true;
        if (wanted != _services.Settings.OverlayDisabled)
            _overlay.ToggleFromTray(HotkeyAction.ToggleOverlay);
        ShowOverlayDisabled(_services.Settings.OverlayDisabled);
    }

    /// <summary>A panel check box or slider changed: the Save button follows, and a running test overlay shows the new values at once.</summary>
    private void OnOverlayCheckChanged(object sender, RoutedEventArgs e) => OverlayValuesChanged();

    private void OnOverlaySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => OverlayValuesChanged();

    private void OverlayValuesChanged()
    {
        if (!_ready)
            return; // the controls are still being filled
        UpdateSaveButton();
        _overlay.UpdateTestSettings(BuildSettings());
    }

    /// <summary>"Show test overlay (15 s)": shows or stops the sample overlay, using the values on screen (saved or not).</summary>
    private void OnTestOverlay(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_overlay.IsTestRunning)
            {
                _testStoppedByUser = true; // OnTestStateChanged (raised by StopTest) must not write "ended" over "stopped"
                try
                {
                    _overlay.StopTest();
                }
                finally
                {
                    _testStoppedByUser = false;
                }
                SetFooter("Test overlay stopped.");
                return;
            }
            var where = _overlay.StartTest(BuildSettings(), this);
            SetFooter($"Test overlay shown {where} for {OverlayController.TestSeconds} seconds.");
        }
        catch (Exception ex)
        {
            _services.Log.Write(DateTimeOffset.UtcNow, "Test overlay failed: " + ex);
            SetFooter("Could not show the test overlay: " + ex.Message);
        }
    }

    private void OnTestStateChanged(object? sender, EventArgs e)
    {
        if (_isClosed)
            return;
        UpdateTestButton();
        if (!_overlay.IsTestRunning && !_testStoppedByUser)
            SetFooter("Test overlay ended.");
    }

    private void UpdateTestButton()
    {
        TestOverlayButton.Content = _overlay.IsTestRunning
            ? $"Stop test overlay ({_overlay.TestSecondsLeft} s)"
            : $"Show test overlay ({OverlayController.TestSeconds} s)";
    }

    private void OnHostedUrlLostFocus(object sender, KeyboardFocusChangedEventArgs e) => ValidateHostedUrl();

    /// <summary>True when the hosted address box holds a valid https address (the 24-hour check needs one).</summary>
    private bool IsHostedUrlUsable() => HostedConfigUrl.Check(HostedUrlBox.Text) is null;

    /// <summary>
    /// The 24-hour check can be ticked only while the address box holds a valid address. When the address is cleared or not valid the
    /// box is unticked and greyed out; when a valid address appears it is enabled again but stays unticked until the user ticks it.
    /// </summary>
    private void UpdateAutoCheckState()
    {
        var usable = IsHostedUrlUsable();
        if (!usable && HostedAutoCheck.IsChecked == true)
            HostedAutoCheck.IsChecked = false;
        HostedAutoCheck.IsEnabled = usable;
    }

    private void OnHostedUrlTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready)
            return; // the controls are still being filled (LoadSettingsControls updates the check box itself)
        UpdateAutoCheckState();
        UpdateSaveButton();
    }

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

    /// <summary>"Refresh Cache (long time)": the full refresh of everything on the saved lists. The button is off while it runs; the result goes to the footer.</summary>
    private async void OnRefreshCache(object sender, RoutedEventArgs e)
    {
        if (_updater.IsRefreshing)
            return;

        SetFooter("Refreshing the cache… this takes a long time (about one request per second, slower if RSI asks us to wait).");
        var message = await _updater.RunFullRefreshAsync(); // greys the button out through RefreshingChanged; null when IRIS is quitting
        if (message is null)
            return;
        if (_isClosed)
            _services.Notify?.Invoke(message); // the window was closed meanwhile: nobody would see the footer
        else
            SetFooter(message);
    }

    private void OnRefreshingChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnRefreshingChanged(sender, e)));
            return;
        }
        RefreshCacheButton.IsEnabled = !_updater.IsRefreshing;
    }

    // ---------------------------------------------------------------------------------------
    // Save button, error banners, status line
    // ---------------------------------------------------------------------------------------

    /// <summary>A row was added, removed or moved: follow its edits from now on (or stop), and see whether Save is needed.</summary>
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (var row in e.OldItems.OfType<INotifyPropertyChanged>())
                row.PropertyChanged -= OnRowPropertyChanged;
        if (e.NewItems is not null)
            foreach (var row in e.NewItems.OfType<INotifyPropertyChanged>())
                row.PropertyChanged += OnRowPropertyChanged;
        UpdateSaveButton();
    }

    /// <summary>A value of a row changed (text, color, priority, type). Status texts and notes change all the time and do not matter here.</summary>
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ListRowViewModel.Value):
            case nameof(ListRowViewModel.Color):
            case nameof(OrgRowViewModel.Priority):
            case nameof(RuleRowViewModel.Kind):
                UpdateSaveButton();
                break;
        }
    }

    /// <summary>Any control of the Settings tab changed.</summary>
    private void OnSettingChanged(object sender, RoutedEventArgs e) => UpdateSaveButton();

    /// <summary>
    /// Save is on while anything differs from what is saved (rows, their order, any value, any setting), and never while an import,
    /// export or save is running. It is ALSO on while a watched file on disk has errors, even when nothing was edited. This is intentional:
    /// saving is how the user repairs a broken file (the window shows the version IRIS is using; Save asks, keeps a copy of the broken
    /// file as ".invalid-<time>" and replaces it).
    /// </summary>
    private void UpdateSaveButton()
    {
        if (!_ready || _isClosed)
            return;
        SaveButton.IsEnabled = _busyCount == 0
            && (HasUnsavedChanges(ignoreHostedUrl: false) || _services.ListFileHasErrors || _services.SettingsFileNeedsSave);
    }

    private void RefreshStateUi()
    {
        UpdateBanners();
        UpdateSaveButton();
    }

    private void UpdateBanners()
    {
        ShowBanner(ListBanner, ListBannerText, DataPaths.SocialListFileName, _services.ListFileErrors, _services.ListFileNote);
        ShowBanner(SettingsBanner, SettingsBannerText, DataPaths.SettingsFileName, _services.SettingsFileErrors, _services.SettingsFileNote);
    }

    private static void ShowBanner(Border banner, TextBlock text, string fileName, IReadOnlyList<string> errors, string note)
    {
        if (errors.Count == 0)
        {
            banner.Visibility = Visibility.Collapsed;
            return;
        }

        const int shown = 3;
        var message = new StringBuilder();
        message.Append(fileName).Append(" has errors. ").Append(note);
        foreach (var error in errors.Take(shown))
            message.Append("\n  - ").Append(error);
        if (errors.Count > shown)
            message.Append($"\n  ...and {errors.Count - shown} more (open the file to see them all).");
        text.Text = message.ToString();
        banner.Visibility = Visibility.Visible;
    }

    private void OnOpenListFile(object sender, RoutedEventArgs e) => OpenInEditor(_services.Paths.SocialListFile);

    private void OnOpenSettingsFile(object sender, RoutedEventArgs e) => OpenInEditor(_services.Paths.SettingsFile);

    /// <summary>Opens a file in its default editor; Notepad when .toml has no program.</summary>
    private void OpenInEditor(string path)
    {
        try
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
                // no program is associated with .toml files
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            SetFooter("Could not open " + Path.GetFileName(path) + ": " + ex.Message);
        }
    }

    /// <summary>Shows a message in the footer (and as a tooltip, because the footer trims long text).</summary>
    private void SetFooter(string message)
    {
        FooterStatus.Text = message;
        FooterStatus.ToolTip = message;
    }

    /// <summary>Shows a message in the footer from outside the window (for example the end of the background fill-missing refresh).</summary>
    public void ShowStatus(string message)
    {
        if (!_isClosed)
            SetFooter(message);
    }

    // ---------------------------------------------------------------------------------------
    // Watching the config files
    // ---------------------------------------------------------------------------------------

    /// <summary>True when nothing is going on that a reload of the rows would disturb.</summary>
    private bool IsIdle() => !_saving && _busyCount == 0 && !_imports.IsBusy && !_closePromptOpen;

    /// <summary>
    /// Every 2 seconds while the window is open: look at the config files (on a background thread, the files are only stat'ed unless
    /// they changed) and apply what is found here. A result that arrives while a save or import is going on waits for the next idle tick.
    /// </summary>
    private async void OnWatchTick(object? sender, EventArgs e)
    {
        if (_isClosed || _polling || _closePromptOpen)
            return;

        _polling = true;
        try
        {
            var poll = _pendingPoll;
            if (poll is null)
            {
                if (!IsIdle())
                    return;
                poll = await Task.Run(() => _services.PollConfigFiles());
            }

            if (!_isClosed && !IsIdle())
            {
                _pendingPoll = poll; // the detector has already moved on: keep the result until it can be shown
                return;
            }

            _pendingPoll = null;
            var changes = _services.ApplyPoll(poll);
            if (!_isClosed)
                ShowConfigChanges(changes);
        }
        catch (Exception ex)
        {
            _services.Log.Write(DateTimeOffset.UtcNow, "Checking the config files failed: " + ex);
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>
    /// Brings the window up to date with what <see cref="AppServices.ApplyPoll"/> applied. A new list replaces ALL rows and a new settings
    /// file replaces ALL settings controls: unsaved edits in the window are discarded (a deliberate choice). Name checks start again for the loaded rows.
    /// </summary>
    private void ShowConfigChanges(ConfigChanges changes)
    {
        if (changes.ListChanged)
        {
            LoadRowsFrom(_services.CurrentConfig);
            _baseline = Snapshot();
        }
        if (changes.SettingsChanged)
            LoadSettingsControls();
        if (changes.ExclusionsChanged)
        {
            foreach (var row in _players)
                row.RefreshExclusionNote();
        }

        RefreshStateUi();
        if (!_services.ListFileMissing && FooterStatus.Text == MissingListNotice)
            SetFooter(string.Empty); // the list file is back (or was saved again): the notice no longer applies
        if (changes.Message is { } message)
            SetFooter(message);
    }

    // ---------------------------------------------------------------------------------------
    // Import and export
    // ---------------------------------------------------------------------------------------

    private async void OnImportFromFile(object sender, RoutedEventArgs e)
    {
        if (_busyCount > 0)
            return;

        // Busy from before the file dialog opens: while it is open the window cannot be closed, saved over, or reloaded by a file check.
        BeginBusy();
        try
        {
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

            var attempt = await _imports.ImportTextAsync(read.Text, "Import " + Path.GetFileName(dialog.FileName), ignoreHostedUrl: false, userJustActed: true);
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
        // Read now: ReloadAfterImport (inside the import) resets the controls from the stored settings.
        var wantAutoCheck = HostedAutoCheck.IsChecked == true;
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
            if (string.Equals(hash, _services.Settings.HostedConfigLastHash, StringComparison.OrdinalIgnoreCase))
            {
                // The same version as the one imported last time: nothing to do.
                const string unchanged = ConfigMessages.HostedConfigUnchanged;
                _services.Log.Write(_services.Clock.UtcNow, "Hosted config unchanged since the last import.");
                _services.Notify?.Invoke(unchanged); // always a notification (tray balloon) ...
                if (!_isClosed)
                    SetFooter(unchanged); // ... and the status bar while the window is open
                return;
            }

            var attempt = await _imports.ImportTextAsync(fetched.Text, "Import from " + new Uri(url).Host, ignoreHostedUrl: true, userJustActed: true);
            if (attempt.Outcome != ImportOutcome.Imported)
            {
                FooterStatus.Text = attempt.Message;
                return;
            }

            // Remember the address and this version, so the next "Import from URL" can tell whether the hosted file has changed.
            // The 24-hour check goes with the address (it was left out of the save above, like the address): the tick the user had when
            // pressing the button (read before the import reset the controls) is stored together with the address.
            var remembered = _services.UpdateSettings(
                s => s with { HostedConfigUrl = url, HostedConfigLastHash = hash, HostedConfigAutoCheck = wantAutoCheck });
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

            // Writing the export onto IRIS's own files would replace them behind the app's back (and be taken for an outside edit).
            if (_services.Paths.IsOwnConfigFile(dialog.FileName))
            {
                FooterStatus.Text = "Export cancelled.";
                MessageBox.Show(
                    this,
                    "That is one of IRIS's own files (" + Path.GetFileName(dialog.FileName) + "), and exporting onto it would overwrite your "
                    + "current " + (string.Equals(Path.GetFileName(dialog.FileName), DataPaths.SettingsFileName, StringComparison.OrdinalIgnoreCase)
                        ? "settings" : "lists")
                    + ".\n\nNothing was exported. Choose another file name or folder.",
                    "IRIS", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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

    /// <summary>No save, import, export or question of the window is open (see <see cref="IImportHost.IsIdleForPrompt"/>).</summary>
    public bool IsIdleForPrompt => IsIdle();

    /// <summary>Lists differ from the last save, or the settings controls differ from the saved settings.</summary>
    public bool HasUnsavedChanges(bool ignoreHostedUrl)
    {
        if (Snapshot() != _baseline)
            return true;

        var shown = BuildSettings();
        var saved = _services.Settings;
        if (ignoreHostedUrl)
            shown = shown with { HostedConfigUrl = saved.HostedConfigUrl, HostedConfigAutoCheck = saved.HostedConfigAutoCheck };
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
        UpdateSaveButton();
    }

    /// <summary>The app's lists were replaced by an import: show them, check every name, show the settings as saved, and start clean.</summary>
    public void ReloadAfterImport(string status)
    {
        if (_isClosed)
            return;

        LoadRowsFrom(_services.CurrentConfig);
        LoadSettingsControls();
        _baseline = Snapshot();
        RefreshStateUi();
        SetFooter(status);
    }

    private static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";
}
