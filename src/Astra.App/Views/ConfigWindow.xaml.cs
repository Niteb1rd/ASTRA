using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
        Closed += (_, _) => _closing.Cancel();
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
    private void RecheckDuplicates(IEnumerable<ListRowViewModel> rows)
    {
        foreach (var row in rows.Where(r => r.IsFlaggedDuplicate).ToList())
        {
            row.ResetStatus();
            _ = CheckRowAsync(row);
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

    private void OnValueLostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ListRowViewModel row)
            _ = CheckRowAsync(row);
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

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var problems = new List<string>();
        CollectNameProblems("Players", _players, RowValidator.CheckHandleFormat, problems);
        CollectNameProblems("Orgs", _orgs, RowValidator.CheckSidFormat, problems);
        for (int i = 0; i < _rules.Count; i++)
        {
            var rule = _rules[i];
            if (rule.NeedsValue && rule.Value.Trim().Length == 0)
                problems.Add($"Org rules, row {i + 1}: the {rule.Kind} rule needs a value.");
        }

        if (problems.Count > 0)
        {
            MessageBox.Show(
                this,
                "These rows must be fixed before saving:\n\n" + string.Join("\n", problems.Take(15))
                + (problems.Count > 15 ? $"\n...and {problems.Count - 15} more." : string.Empty),
                "ASTRA", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var nameRows = NameRows().ToList();
        var checking = nameRows.Count(r => r.StatusKind == RowStatusKind.Checking);
        if (checking > 0)
        {
            MessageBox.Show(
                this,
                $"{Plural(checking, "name is", "names are")} still being checked. Wait a moment and press Save again.",
                "ASTRA", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
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
                return;
        }

        var config = BuildConfig();
        var saved = SocialListStore.Save(_services.Paths.SocialListFile, config);
        if (!saved.Success)
        {
            MessageBox.Show(this, saved.Error ?? "The list could not be saved.", "ASTRA", MessageBoxButton.OK, MessageBoxImage.Error);
            FooterStatus.Text = "Not saved.";
            return;
        }

        _services.SetCurrentConfig(config);
        _baseline = Snapshot();
        FooterStatus.Text = "Saved at " + DateTime.Now.ToString("HH:mm:ss") + ".";
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
