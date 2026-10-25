using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using TrackTelemetryViewer;

namespace PitWallApp;

// Loading overlay counter (ported from the WPF BeginLoading/EndLoading) and tree state preservation.
public sealed partial class MainWindow
{
    private int _activeLoads;
    private bool _closing;

    private void BeginLoading(string detail)
    {
        _activeLoads++;
        LoadingDetailText.Text = detail;
        LoadingIndicator.IsVisible = true;
    }

    private void UpdateLoadingDetail(string detail)
    {
        if (_activeLoads > 0)
        {
            LoadingDetailText.Text = detail;
        }
    }

    private void EndLoading()
    {
        _activeLoads = Math.Max(0, _activeLoads - 1);
        if (_activeLoads == 0)
        {
            LoadingIndicator.IsVisible = false;
            LoadingDetailText.Text = string.Empty;
        }
    }

    private static void CollectExpanded(IEnumerable<TreeNode> nodes, HashSet<string> keys)
    {
        foreach (var node in nodes)
        {
            if (node.IsExpanded && node.Key is { } key)
            {
                keys.Add(key);
                CollectExpanded(node.Children, keys);
            }
        }
    }

    // Rebuilds the tree; with preserve, expanded nodes and the selection are restored by node identity.
    private async Task RebuildTreeAsync(bool preserve)
    {
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        string? selectedKey = null;
        if (preserve)
        {
            CollectExpanded(_roots, expanded);
            selectedKey = (SessionTree.SelectedItem as TreeNode)?.Key;
        }

        var generation = ++_rebuildGeneration;
        _restoring = true;
        try
        {
            BuildTree(!preserve);
            if (!preserve)
            {
                return;
            }

            await RestoreExpandedAsync(_roots, expanded, generation);
            if (generation != _rebuildGeneration)
            {
                return;
            }

            if (selectedKey is not null && FindPath(node => node.Key == selectedKey) is { } route)
            {
                SessionTree.SelectedItem = route[^1];
                Dispatcher.UIThread.Post(() =>
                    (SessionTree.TreeContainerFromItem(route[^1]) as Control)?.BringIntoView());
            }
        }
        finally
        {
            if (generation == _rebuildGeneration)
            {
                _restoring = false;
            }
        }

        if (!preserve || generation != _rebuildGeneration)
        {
            return;
        }

        SetReferenceButton.IsEnabled = SelectedLap is not null;
        if (Overview.IsVisible)
        {
            if (SessionTree.SelectedItem is TreeNode)
            {
                await HandleSelectionAsync();
            }
            else
            {
                ResetOverview();
            }
        }
    }

    private async Task RestoreExpandedAsync(IEnumerable<TreeNode> nodes, HashSet<string> keys, int generation)
    {
        foreach (var node in nodes.ToArray())
        {
            if (generation != _rebuildGeneration || _closing)
            {
                return;
            }

            if (node.Key is { } key && keys.Contains(key))
            {
                node.IsExpanded = true;
                await node.LoadTask;
                await RestoreExpandedAsync(node.Children, keys, generation);
            }
        }
    }

    private static bool SameMap(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count &&
        left.All(pair => right.TryGetValue(pair.Key, out var value) &&
                         string.Equals(pair.Value, value, StringComparison.Ordinal));

    // True when a settings change alters what the tree shows (names, grouping or hidden vehicles).
    private static bool AffectsTree(ViewerSettings before, ViewerSettings after) =>
        !SameMap(before.VehicleMappings, after.VehicleMappings) ||
        !SameMap(before.SessionVehicleMappings, after.SessionVehicleMappings) ||
        !SameMap(before.SessionLayoutAssignments, after.SessionLayoutAssignments) ||
        !SameMap(before.TrackMappings, after.TrackMappings) ||
        !new HashSet<string>(before.HiddenVehicleNames, StringComparer.CurrentCultureIgnoreCase)
            .SetEquals(after.HiddenVehicleNames);
}
