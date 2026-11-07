using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrackTelemetryViewer;

namespace PitWallApp;

// Context menus and corrections, matching the WPF menus node for node.
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
            AddLapItems(items, lap);
        }
        else if (node.Session is { } session)
        {
            items.Add(Item("Use fastest lap as reference", async () => await UseFastestAsReferenceAsync([session])));
            items.Add(Item("Session details…", async () => await EditSessionAsync(session)));
            items.Add(CreateAssignVehicleMenu("Assign vehicle to this session", [], [session.SessionGuid], [session]));
            AddSessionTrackAssignmentMenu(items, session);
            items.Add(Item("Export…", async () =>
                await ExportAsync([session], session.LocalStart.ToString("yyyy-MM-dd HH.mm"))));
        }
        else if (node.Date is { } date)
        {
            items.Add(Item("Use fastest lap as reference", async () => await UseFastestAsReferenceAsync(date.Sessions)));
            items.Add(CreateAssignVehicleMenu(
                "Assign vehicle to this date's sessions",
                [],
                date.Sessions.Select(session => session.SessionGuid).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                date.Sessions));
            items.Add(Item("Export…", async () => await ExportAsync(date.Sessions, date.Date.ToString("yyyy-MM-dd"))));
        }
        else if (node.Driver is { OptimalLap: { } optimal })
        {
            AddLapItems(items, optimal);
        }
        else if (node.Car is { } car)
        {
            var aliases = car.Sessions.Select(session => session.VehicleAlias)
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var sessionGuids = car.Sessions.Where(session => session.AiMData is not null)
                .Select(session => session.SessionGuid)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            items.Add(CreateAssignVehicleMenu("Assign vehicle name", aliases, sessionGuids, car.Sessions));
        }
        else if (node.Location is { } location)
        {
            if (_model.GetAssignableLayouts(location) is { Count: > 0 } layouts)
            {
                var parent = new MenuItem { Header = "Choose layout" };
                foreach (var layout in layouts)
                {
                    parent.Items.Add(Item(layout, async () => await AssignLayout(location, layout)));
                }

                items.Add(parent);
            }

            AddCsvTrackItems(items, location);
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

    private void AddLapItems(List<Control> items, LapInfo lap)
    {
        items.Add(Item("Use as reference lap", async () => await SetReferenceLapAsync(lap)));
        items.Add(Item("Clear reference lap", ClearReference, _referenceLap is not null));
    }

    private void AddSessionTrackAssignmentMenu(List<Control> items, SessionInfo session)
    {
        var parent = new MenuItem { Header = "Assign session to track" };
        var current = _model.LocationOf(session);
        foreach (var target in _model.Locations.Where(location => !ReferenceEquals(location, current))
                     .OrderBy(location => location.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var representative = target.Sessions.FirstOrDefault(candidate => !SessionModel.IsCsvSession(candidate)) ??
                                 target.Sessions.FirstOrDefault();
            if (representative is not null)
            {
                parent.Items.Add(Item(target.DisplayName,
                    async () => await AssignSessionTrack(session, representative.SessionGuid)));
            }
        }

        if (_model.Settings.SessionTrackAssignments.ContainsKey(session.SessionGuid))
        {
            if (parent.Items.Count > 0)
            {
                parent.Items.Add(new Separator());
            }

            parent.Items.Add(Item("Use detected track", async () => await AssignSessionTrack(session, string.Empty)));
        }

        if (parent.Items.Count > 0)
        {
            items.Add(parent);
        }
    }

    private async Task AssignSessionTrack(SessionInfo session, string targetSessionGuid)
    {
        var updated = SettingsStore.Clone(_model.Settings);
        if (string.IsNullOrWhiteSpace(targetSessionGuid))
        {
            updated.SessionTrackAssignments.Remove(session.SessionGuid);
        }
        else
        {
            updated.SessionTrackAssignments[session.SessionGuid] = targetSessionGuid;
        }

        if (!SaveSettings(updated, "Could not save track assignment"))
        {
            return;
        }

        _model.BuildTrackLocations();
        await RebuildTreeAsync(true);
        StatusText.Text = string.IsNullOrWhiteSpace(targetSessionGuid)
            ? "The session now uses its detected track."
            : "The session was assigned to the selected track.";
    }

    private static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private MenuItem CreateAssignVehicleMenu(
        string header, IReadOnlyList<string> aliases, IReadOnlyList<string> sessionGuids,
        IReadOnlyList<SessionInfo> sessions)
    {
        var canAssign = aliases.Count > 0 || sessionGuids.Count > 0;
        var parent = new MenuItem { Header = header };
        foreach (var name in _model.Catalog.Names
                     .Concat(_model.Settings.CustomVehicleNames)
                     .Where(name => !string.IsNullOrWhiteSpace(name) && !_model.IsVehicleHidden(name))
                     .Distinct(StringComparer.CurrentCultureIgnoreCase)
                     .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
        {
            parent.Items.Add(Item(name, async () => await AssignVehicleName(aliases, sessionGuids, name, sessions),
                canAssign));
        }

        if (parent.Items.Count == 0)
        {
            parent.Items.Add(new MenuItem { Header = "No catalog vehicles available", IsEnabled = false });
        }

        parent.Items.Add(new Separator());
        parent.Items.Add(Item("Other name…", async () =>
        {
            var initial = sessions.Count > 0 ? _model.GetVehicleName(sessions[0]) : string.Empty;
            var name = await Dialogs.PromptAsync(this, header, "Vehicle name", initial);
            if (!string.IsNullOrWhiteSpace(name))
            {
                await AssignVehicleName(aliases, sessionGuids, name, sessions);
            }
        }, canAssign));
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
        IReadOnlyList<string> aliases, IReadOnlyList<string> sessionGuids, string vehicleName,
        IReadOnlyList<SessionInfo> sessions)
    {
        var updated = SettingsStore.Clone(_model.Settings);
        foreach (var alias in aliases)
        {
            updated.VehicleMappings[alias] = vehicleName;
        }

        foreach (var session in sessions)
        {
            updated.SessionVehicleMappings.Remove(session.SessionGuid);
            updated.SessionVehicleMappings[session.VehicleAssignmentKey] = vehicleName;
        }

        // A car-level assignment applies through the alias, so drop per-session overrides it would hide.
        if (aliases.Count > 0)
        {
            foreach (var session in sessions.Where(session => !string.IsNullOrWhiteSpace(session.VehicleAlias)))
            {
                updated.SessionVehicleMappings.Remove(session.SessionGuid);
            }
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
            SelectNode(node => node.Car is { } group && group.Sessions.Any(sessions.Contains));
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

        UpdateLapCards();
        StatusText.Text = "Session vehicle and conditions saved.";
    }

    // Session and date menus: the fastest driven lap across the given sessions becomes the reference.
    private async Task UseFastestAsReferenceAsync(IReadOnlyList<SessionInfo> sessions)
    {
        BeginLoading($"Reading laps from {sessions[0].FileName}");
        try
        {
            var candidates = new List<LapInfo>();
            foreach (var session in sessions)
            {
                candidates.AddRange(await GetLapsAsync(session));
            }

            if (FastestDrivenLap(candidates) is not { } fastest)
            {
                await Dialogs.ShowMessageAsync(this, "Reference lap unavailable", "No fastest driven lap was found.");
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
        if (_primaryLap is not null && _model.LocationOf(_primaryLap.Session) != _model.LocationOf(lap.Session))
        {
            await Dialogs.ShowMessageAsync(this, "Reference must be from the same track",
                "Choose a reference lap from the same track as the selected lap.");
            return;
        }

        _referenceLap = lap;
        ClearReferenceButton.IsEnabled = true;
        UpdateLapCards();
        if (_primaryLap is null)
        {
            return;
        }

        BeginLoading($"Loading reference {lap.Session.FileName} · {lap.Label}");
        try
        {
            var trace = await Task.Run(() => TelemetryLoader.ReadTrace(lap));
            if (_referenceLap != lap)
            {
                return;
            }

            _reference = trace;
            RefreshChart();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or JsonException or ArgumentException)
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
        _referenceLap = null;
        _reference = null;
        ClearReferenceButton.IsEnabled = false;
        RefreshChart();
    }

    private async Task ShowSettingsAsync()
    {
        if (_dataFolder is null)
        {
            await Dialogs.ShowMessageAsync(this, "Settings", "Open a data folder first. Settings are saved beside the data.");
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

        if (dialog.VehicleRenames.Count > 0)
        {
            var editsChanged = false;
            foreach (var sessionGuid in _model.Edits.Keys.ToArray())
            {
                var edits = _model.Edits[sessionGuid];
                var rename = dialog.VehicleRenames.FirstOrDefault(pair =>
                    pair.Key.Equals(edits.VehicleName, StringComparison.CurrentCultureIgnoreCase));
                if (!string.IsNullOrEmpty(rename.Key))
                {
                    _model.Edits[sessionGuid] = edits with { VehicleName = rename.Value };
                    editsChanged = true;
                }
            }

            if (editsChanged && SettingsStore.SaveEdits(_model.Edits) is { } editsError)
            {
                await Dialogs.ShowMessageAsync(this, "Could not save session names", editsError);
            }
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
