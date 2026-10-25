using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrackTelemetryViewer;

namespace PitWallApp;

public sealed partial class MainWindow
{
    private void OnTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Visual source ||
            (source as TreeViewItem ?? source.FindAncestorOfType<TreeViewItem>()) is not { } item ||
            item.DataContext is not TreeNode node ||
            CreateContextMenu(node) is not { } menu)
        {
            return;
        }

        menu.Open(item);
        e.Handled = true;
    }

    private ContextMenu? CreateContextMenu(TreeNode node)
    {
        List<Control> items = [];
        if (node.Lap is { } lap)
        {
            items.Add(Item("Use as reference lap", async () => await SetReferenceLapAsync(lap)));
            items.Add(Item("Clear reference lap", () => ClearReference(), _reference is not null));
        }
        else if (node.Session is { } session)
        {
            items.Add(Item("Use fastest lap as reference", async () => await UseSessionFastestAsReferenceAsync(session)));
            items.Add(Item("Edit session details…", async () => await EditSessionAsync(session)));
        }
        else if (node.Car is { } car)
        {
            items.Add(CreateVehicleMenu(car));
        }
        else if (node.Location is { } location && _model.GetAssignableLayouts(location) is { Count: > 0 } layouts)
        {
            var parent = new MenuItem { Header = "Choose layout" };
            foreach (var layout in layouts)
            {
                parent.Items.Add(Item(layout, async () => await AssignLayout(location, layout)));
            }

            items.Add(parent);
        }

        if (items.Count == 0)
        {
            return null;
        }

        var menu = new ContextMenu();
        foreach (var item in items)
        {
            menu.Items.Add(item);
        }

        return menu;
    }

    private static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private MenuItem CreateVehicleMenu(TrackCarGroup car)
    {
        var aliases = car.Sessions.Select(session => session.VehicleAlias)
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sessionGuids = car.Sessions.Where(session => session.AiMData is not null)
            .Select(session => session.SessionGuid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var canAssign = aliases.Length > 0 || sessionGuids.Length > 0;
        var parent = new MenuItem { Header = "Assign vehicle name", IsEnabled = canAssign };
        foreach (var name in VehicleChoices())
        {
            parent.Items.Add(Item(name, async () => await AssignVehicleName(car, aliases, sessionGuids, name)));
        }

        parent.Items.Add(new Separator());
        parent.Items.Add(Item("Other name…", async () =>
        {
            var name = await Dialogs.PromptAsync(this, "Assign vehicle name", "Vehicle name", car.Name);
            if (!string.IsNullOrWhiteSpace(name))
            {
                await AssignVehicleName(car, aliases, sessionGuids, name);
            }
        }));
        return parent;
    }

    private IReadOnlyList<string> VehicleChoices() => _model.Catalog.Names
        .Concat(_model.Settings.CustomVehicleNames)
        .Concat(_model.Settings.VehicleMappings.Values)
        .Where(name => !string.IsNullOrWhiteSpace(name) && !_model.IsVehicleHidden(name))
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    private bool SaveSettings(ViewerSettings updated, string failureTitle)
    {
        if (_dataFolder is null)
        {
            return false;
        }

        if (SettingsStore.Save(_dataFolder, updated) is { } error)
        {
            _ = Dialogs.ShowMessageAsync(this, failureTitle, error);
            return false;
        }

        _model.Settings = updated;
        return true;
    }

    private async Task AssignLayout(TrackLocation location, string layoutName)
    {
        var updated = SettingsStore.Clone(_model.Settings);
        foreach (var session in location.Sessions)
        {
            updated.SessionLayoutAssignments[session.SessionGuid] = layoutName;
        }

        if (!SaveSettings(updated, "Could not save layout"))
        {
            return;
        }

        _model.BuildTrackLocations();
        await RebuildTreeAsync(true);
        var first = location.Sessions[0];
        if (SessionTree.SelectedItem is null)
        {
            SelectNode(node => node.Location is { } candidate && candidate.Sessions.Contains(first));
        }

        StatusText.Text = $"Layout set to {layoutName} and saved to {SettingsStore.ViewerSettingsPath(_dataFolder!)}.";
    }

    private async Task AssignVehicleName(
        TrackCarGroup car, IReadOnlyList<string> aliases, IReadOnlyList<string> sessionGuids, string vehicleName)
    {
        var updated = SettingsStore.Clone(_model.Settings);
        foreach (var alias in aliases)
        {
            updated.VehicleMappings[alias] = vehicleName;
        }

        foreach (var guid in sessionGuids)
        {
            updated.SessionVehicleMappings[guid] = vehicleName;
        }

        updated.CustomVehicleNames = updated.CustomVehicleNames.Append(vehicleName)
            .Where(name => !_model.Catalog.Names.Contains(name, StringComparer.CurrentCultureIgnoreCase))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (!SaveSettings(updated, "Could not save vehicle mapping"))
        {
            return;
        }

        await RebuildTreeAsync(true);
        if (SessionTree.SelectedItem is null)
        {
            SelectNode(node => node.Car is { } group && group.Sessions.Any(session => car.Sessions.Contains(session)));
        }

        StatusText.Text = $"Assigned {vehicleName} to {aliases.Count + sessionGuids.Count} vehicle source(s).";
    }

    private async Task EditSessionAsync(SessionInfo session)
    {
        var dialog = new SessionDetailsWindow(_model, session, VehicleChoices());
        if (await dialog.ShowDialog<SessionDetailsResult?>(this) is not { } result)
        {
            return;
        }

        var trackCondition = string.IsNullOrWhiteSpace(result.TrackCondition)
            ? "UNKNOWN"
            : result.TrackCondition.Trim().ToUpperInvariant();
        var conditionChanged = !trackCondition.Equals(
            _model.GetSessionEdits(session).TrackCondition, StringComparison.OrdinalIgnoreCase);
        var metadataNeedsUpdate = !trackCondition.Equals(session.TrackCondition, StringComparison.OrdinalIgnoreCase);
        if (session.AiMData is null && metadataNeedsUpdate && conditionChanged &&
            string.IsNullOrWhiteSpace(session.SessionMetadataPath))
        {
            await Dialogs.ShowMessageAsync(this, "Session metadata unavailable",
                "This session has no metadata file to update, so its track condition cannot be changed.");
            return;
        }

        if (metadataNeedsUpdate && !string.IsNullOrWhiteSpace(session.SessionMetadataPath))
        {
            try
            {
                TelemetryLoader.WriteTrackCondition(session.SessionMetadataPath, trackCondition);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               JsonException or InvalidDataException or ArgumentException)
            {
                await Dialogs.ShowMessageAsync(this, "Could not save track condition",
                    $"The track condition could not be saved to {session.SessionMetadataPath}.\n{exception.Message}");
                return;
            }
        }

        var updated = SettingsStore.Clone(_model.Settings);
        if (string.IsNullOrWhiteSpace(result.LayoutValue) ||
            result.LayoutValue.Equals(SessionModel.UseSourceLayout, StringComparison.OrdinalIgnoreCase))
        {
            updated.SessionLayoutAssignments.Remove(session.SessionGuid);
        }
        else
        {
            updated.SessionLayoutAssignments[session.SessionGuid] = result.LayoutValue;
        }

        if (!updated.SessionLayoutAssignments.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(_model.Settings.SessionLayoutAssignments
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)))
        {
            if (!SaveSettings(updated, "Could not save track assignment"))
            {
                return;
            }

            _model.BuildTrackLocations();
        }

        _model.Edits[session.SessionGuid] = new SessionEdits(
            result.VehicleName, trackCondition, result.RaceType, result.DriverName);
        if (SettingsStore.SaveEdits(_model.Edits) is { } error)
        {
            await Dialogs.ShowMessageAsync(this, "Could not save session details", error);
            return;
        }

        await RebuildTreeAsync(true);
        if (SessionTree.SelectedItem is null)
        {
            SelectNode(node => node.Session?.SessionGuid == session.SessionGuid);
        }

        StatusText.Text = "Session details saved.";
    }

    private async Task UseSessionFastestAsReferenceAsync(SessionInfo session)
    {
        BeginLoading($"Reading laps from {session.FileName}");
        try
        {
            var laps = await Task.Run(() => TelemetryLoader.ReadLaps(session));
            var fastest = laps.Where(lap => !lap.IsAverage && double.IsFinite(lap.DurationMs) && lap.DurationMs > 0)
                .MinBy(lap => lap.DurationMs);
            if (fastest is null)
            {
                await Dialogs.ShowMessageAsync(this, "Reference lap unavailable",
                    "No fastest driven lap was found in this session.");
                return;
            }

            await SetReferenceLapAsync(fastest);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           JsonException or InvalidDataException or ArgumentException)
        {
            await Dialogs.ShowMessageAsync(this, "Reference lap unavailable", exception.Message);
        }
        finally
        {
            EndLoading();
        }
    }

    private async Task SetReferenceLapAsync(LapInfo lap)
    {
        if (_primary is not null && _model.LocationOf(_primary.Session) != _model.LocationOf(lap.Session))
        {
            await Dialogs.ShowMessageAsync(this, "Reference must be from the same track",
                "Choose a reference lap from the same track as the selected lap.");
            return;
        }

        BeginLoading($"Loading reference {lap.Session.FileName} · {lap.Label}");
        try
        {
            _reference = await Task.Run(() => TelemetryLoader.ReadTrace(lap));
            ClearReferenceButton.IsEnabled = true;
            RefreshChart();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or JsonException)
        {
            StatusText.Text = $"Could not read reference lap: {exception.Message}";
        }
        finally
        {
            EndLoading();
        }
    }

    private void ClearReference()
    {
        _reference = null;
        ClearReferenceButton.IsEnabled = false;
        RefreshChart();
    }

    private async Task ShowSettingsAsync()
    {
        if (_dataFolder is null)
        {
            return;
        }

        var aliases = _model.Sessions.Select(session => session.VehicleAlias)
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Concat(_model.Settings.VehicleMappings.Keys);
        var vehicleNames = _model.Catalog.Names.Concat(_model.Settings.CustomVehicleNames)
            .Concat(_model.Settings.VehicleMappings.Values);
        var previous = _model.Settings;
        var dialog = new SettingsWindow(
            _model.Settings, aliases, _model.BuildTrackAliasSettings(), vehicleNames, _model.Catalog.Names,
            ChannelCatalog.Build(_aimChannels, _model.Settings));
        if (await dialog.ShowDialog<ViewerSettings?>(this) is not { } updated ||
            !SaveSettings(updated, "Could not save settings"))
        {
            return;
        }

        _loading = true;
        UnitsBox.SelectedIndex = updated.DistanceUnits.Equals("Imperial", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _loading = false;
        RefreshChart();
        if (AffectsTree(previous, updated))
        {
            _model.BuildTrackLocations();
            await RebuildTreeAsync(true);
        }

        UpdateOverviewLabels();
        StatusText.Text = $"Settings saved to {SettingsStore.ViewerSettingsPath(_dataFolder)}.";
    }

    private List<TreeNode>? FindPath(Func<TreeNode, bool> match)
    {
        List<TreeNode>? Search(IEnumerable<TreeNode> nodes, List<TreeNode> path)
        {
            foreach (var node in nodes)
            {
                path.Add(node);
                if (match(node))
                {
                    return path;
                }

                if (Search(node.Children, path) is { } found)
                {
                    return found;
                }

                path.RemoveAt(path.Count - 1);
            }

            return null;
        }

        return Search(_roots, []);
    }

    // Selects the first matching node and expands its ancestors after the tree is rebuilt.
    private void SelectNode(Func<TreeNode, bool> match)
    {
        if (FindPath(match) is not { } route)
        {
            return;
        }

        foreach (var ancestor in route.SkipLast(1))
        {
            ancestor.IsExpanded = true;
        }

        var target = route[^1];
        SessionTree.SelectedItem = target;
        Dispatcher.UIThread.Post(() =>
            (SessionTree.TreeContainerFromItem(target) as Control)?.BringIntoView());
    }
}
