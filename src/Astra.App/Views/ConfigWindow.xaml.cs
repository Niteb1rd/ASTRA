using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
using Astra.App.Services;
using Astra.App.ViewModels;
using Astra.Core.Config;
using Astra.Core.Storage;

namespace Astra.App.Views;

/// <summary>
/// The Social Overlay config window: player, org and org-rule lists. Edits happen on row view models;
/// nothing is written until Save. Names are checked against RSI (cache first) when the user leaves the cell.
/// Later increments add the Settings tab, import/export and the overlay controls to this window.
/// </summary>
public partial class ConfigWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<PlayerRowViewModel> _players = new();
    private readonly ObservableCollection<OrgRowViewModel> _orgs = new();
    private readonly ObservableCollection<RuleRowViewModel> _rules = new();
    private readonly CancellationTokenSource _closing = new();

    // Name checks that are running, per row. Save waits for these (a click on Save first makes the cell lose focus,
    // which starts a check; without waiting, Save would be refused with "still being checked").
    private readonly Dictionary<ListRowViewModel, Task> _checks = new();
    private bool _saving;
    private bool _invalidFileCopied;

    private readonly string? _listName;
    private string _baseline;

    public ConfigWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        var config = services.CurrentConfig;
        _listName = config.Name; // the shared list's name is kept as loaded

        services.Exclusions.ReloadIfChanged();
        foreach (var p in config.Players)
            _players.Add(new PlayerRowViewModel(p.Handle, p.Color, IsExcluded));
        foreach (var o in config.Orgs)
            _orgs.Add(new OrgRowViewModel(o.Sid, o.Priority, o.Color));
        foreach (var r in config.OrgRules)
            _rules.Add(new RuleRowViewModel(r.Kind, r.Value, r.Priority, r.Color));

        PlayersList.ItemsSource = _players;
        OrgsList.ItemsSource = _orgs;
        RulesList.ItemsSource = _rules;

        _baseline = Snapshot();
        Closed += (_, _) =>
        {
            _closing.Cancel();
            _closing.Dispose();
        };

        // Check every loaded name now: fresh cache entries answer at once, the rest go through the shared RSI limiter.
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
    /// Enter in a name box ends editing instead of pressing Save: the keyboard focus moves to the window itself
    /// (a neutral target, so it does not jump into another row), which raises LostKeyboardFocus and starts the check.
    /// </summary>
    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        Keyboard.Focus(this);
    }

    /// <summary>A click on empty space ends editing: a focused text box gives up focus (and so gets checked).</summary>
    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.FocusedElement is not TextBox)
            return;
        if (IsInteractive(e.OriginalSource as DependencyObject))
            return;
        Keyboard.Focus(this);
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
        if (_saving)
            return;
        _saving = true;
        try
        {
            var pending = _checks.Values.Where(t => !t.IsCompleted).ToList();
            if (pending.Count > 0)
            {
                FooterStatus.Text = "Waiting for name checks…";
                await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromSeconds(25), _closing.Token));
                if (_closing.IsCancellationRequested)
                    return; // the window was closed meanwhile
                FooterStatus.Text = string.Empty;
            }

            if (SaveCore())
                Close(); // saved: the baseline was updated, so OnWindowClosing does not ask about unsaved changes
        }
        catch (Exception ex)
        {
            if (!_closing.IsCancellationRequested)
            {
                FooterStatus.Text = "Not saved.";
                MessageBox.Show(this, "The list could not be saved:\n\n" + ex.Message, "ASTRA", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _saving = false;
        }
    }

    /// <summary>Validates and writes the list. Returns true only when the file was written.</summary>
    private bool SaveCore()
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
                "ASTRA", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var nameRows = NameRows().ToList();
        var checking = nameRows.Count(r => r.StatusKind == RowStatusKind.Checking);
        if (checking > 0)
        {
            MessageBox.Show(
                this,
                $"{Plural(checking, "name is", "names are")} still being checked. Wait a moment and press Save again.",
                "ASTRA", MessageBoxButton.OK, MessageBoxImage.Information);
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
                "ASTRA", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                return false;
        }

        if (!KeepCopyOfInvalidFile())
            return false;

        var config = BuildConfig();
        var saved = SocialListStore.Save(_services.Paths.SocialListFile, config);
        if (!saved.Success)
        {
            MessageBox.Show(this, saved.Error ?? "The list could not be saved.", "ASTRA", MessageBoxButton.OK, MessageBoxImage.Error);
            FooterStatus.Text = "Not saved.";
            return false;
        }

        _services.SetCurrentConfig(config);
        _baseline = Snapshot();
        FooterStatus.Text = "Saved at " + DateTime.Now.ToString("HH:mm:ss") + ".";
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
            $"Your list file ({Path.GetFileName(listFile)}) had {Plural(load.Errors.Count, "problem", "problems")} when ASTRA started.\n\n"
            + "Saving replaces the file with what you see in this window. Entries in the file that could not be read will be dropped.\n\n"
            + "A copy of the original file is kept as:\n" + Path.GetFileName(copy) + "\n\nSave now?",
            "ASTRA", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return false;

        try
        {
            File.Copy(listFile, copy, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "A copy of the original file could not be made, so nothing was saved:\n\n" + ex.Message,
                "ASTRA", MessageBoxButton.OK, MessageBoxImage.Error);
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
        if (Snapshot() == _baseline)
            return;
        var answer = MessageBox.Show(
            this, "You have unsaved changes. Discard them?",
            "ASTRA", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
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

    private static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";
}
