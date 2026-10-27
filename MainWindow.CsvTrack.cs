using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace TrackTelemetryViewer;

public partial class MainWindow
{
    private const string CsvVenueKeyPrefix = "venue:";
    private const int CsvAssignmentVersion = 4;

    private readonly Dictionary<string, SessionInfo> _csvOriginals = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CsvVenueAssignment(
        string TrackName,
        string Configuration,
        double Latitude,
        double Longitude,
        AimCsvTransform? Transform,
        int Version = CsvAssignmentVersion);

    private sealed record CsvTrackChoice(string Venue, TrackLocation? Target);

    private static bool IsCsvSession(SessionInfo session) =>
        session.AiMData is not null && session.FilePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

    private static string CsvVenuePrefixedKey(string venue) => CsvVenueKeyPrefix + venue.Trim();

    private CsvVenueAssignment? GetCsvAssignment(string venue)
    {
        if (!_viewerSettings.TrackMappings.TryGetValue(CsvVenuePrefixedKey(venue), out var json))
        {
            return null;
        }

        try
        {
            var assignment = JsonSerializer.Deserialize<CsvVenueAssignment>(json, JsonOptions);
            return assignment is { Transform: not null, Version: >= CsvAssignmentVersion } ? assignment : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private double GetCsvShiftSeconds(string venue) => GetCsvAssignment(venue)?.Transform?.ShiftSeconds ?? 0;

    // CSV exports have no coordinates, so sessions are overlaid onto the assigned track's real GPS lap.
    private void ApplyCsvTrackAssignments()
    {
        for (var index = 0; index < _sessions.Count; index++)
        {
            var current = _sessions[index];
            if (!IsCsvSession(current))
            {
                continue;
            }

            if (!_csvOriginals.TryGetValue(current.FilePath, out var original))
            {
                original = current;
                _csvOriginals[current.FilePath] = original;
            }

            var assignment = GetCsvAssignment(original.AiMData!.TrackName);
            if (assignment?.Transform is not { } transform)
            {
                _sessions[index] = original;
                continue;
            }

            var data = original.AiMData!;
            _sessions[index] = original with
            {
                Latitude = assignment.Latitude,
                Longitude = assignment.Longitude,
                MeanlineTrackName = assignment.TrackName,
                MeanlineConfigurationName = assignment.Configuration,
                AiMData = data with
                {
                    Laps = data.Laps.Select(lap => lap with
                    {
                        Points = AimCsvAlignment.Apply(lap.Points, transform)
                    }).ToArray()
                }
            };
        }
    }

    private ContextMenu? AddCsvTrackMenu(TrackLocation location, ContextMenu? menu)
    {
        var venues = location.Sessions
            .Where(IsCsvSession)
            .Select(session => _csvOriginals.TryGetValue(session.FilePath, out var original) ? original : session)
            .Select(session => session.AiMData!.TrackName.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (venues.Length == 0)
        {
            return menu;
        }

        var targets = _locations
            .Where(candidate => !ReferenceEquals(candidate, location) && candidate.Sessions.Any(session => !IsCsvSession(session)))
            .OrderBy(candidate => candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var assignedVenues = venues.Where(venue => GetCsvAssignment(venue) is not null).ToArray();
        if (targets.Length == 0 && assignedVenues.Length == 0)
        {
            return menu;
        }

        menu ??= CreateThemedContextMenu();
        var itemStyle = (Style)FindResource("DarkMenuItemStyle");
        foreach (var venue in venues)
        {
            if (targets.Length > 0)
            {
                var assignMenu = new MenuItem
                {
                    Header = venues.Length > 1 ? $"Assign “{venue}” to track" : "Assign CSV data to track",
                    Style = itemStyle,
                    ItemContainerStyle = itemStyle
                };
                foreach (var target in targets)
                {
                    var item = new MenuItem
                    {
                        Header = target.DisplayName,
                        Style = itemStyle,
                        Tag = new CsvTrackChoice(venue, target)
                    };
                    item.Click += AssignCsvTrack_Click;
                    assignMenu.Items.Add(item);
                }

                menu.Items.Add(assignMenu);
            }

            if (assignedVenues.Contains(venue, StringComparer.CurrentCultureIgnoreCase))
            {
                var reset = new MenuItem
                {
                    Header = venues.Length > 1 ? $"Unassign “{venue}” (use its own track)" : "Unassign CSV track",
                    Style = itemStyle,
                    Tag = new CsvTrackChoice(venue, null)
                };
                reset.Click += AssignCsvTrack_Click;
                menu.Items.Add(reset);
            }
        }

        return menu;
    }

    private async void AssignCsvTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: CsvTrackChoice choice })
        {
            return;
        }

        var key = CsvVenuePrefixedKey(choice.Venue);
        var mappings = new Dictionary<string, string>(_viewerSettings.TrackMappings, StringComparer.OrdinalIgnoreCase);
        var venueFiles = _csvOriginals.Values
            .Where(session => session.AiMData!.TrackName.Trim().Equals(choice.Venue, StringComparison.CurrentCultureIgnoreCase))
            .Select(session => session.FilePath)
            .ToArray();
        double shiftSeconds = 0;
        if (choice.Target is null)
        {
            mappings.Remove(key);
        }
        else
        {
            var target = choice.Target;
            BeginLoading($"Aligning “{choice.Venue}” to {target.DisplayName}");
            try
            {
                var transform = await Task.Run(() => FitCsvTransform(venueFiles, target));
                if (transform is null)
                {
                    MessageBox.Show(this,
                        "No GPS lap was found at that track to align the CSV data against.",
                        "Could not align", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                shiftSeconds = transform.ShiftSeconds;
                var configuration = target.ConfigurationName is "Combined layouts" or "Layout unspecified"
                    ? string.Empty
                    : target.ConfigurationName;
                mappings[key] = JsonSerializer.Serialize(new CsvVenueAssignment(
                    target.TrackName, configuration, target.Latitude, target.Longitude, transform));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
            {
                MessageBox.Show(this, exception.Message, "Could not align", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally
            {
                EndLoading();
            }
        }

        var updated = new ViewerSettings
        {
            DistanceUnits = _viewerSettings.DistanceUnits,
            CustomVehicleNames = [.. _viewerSettings.CustomVehicleNames],
            HiddenVehicleNames = [.. _viewerSettings.HiddenVehicleNames],
            VehicleMappings = new Dictionary<string, string>(
                _viewerSettings.VehicleMappings, StringComparer.OrdinalIgnoreCase),
            SessionVehicleMappings = new Dictionary<string, string>(
                _viewerSettings.SessionVehicleMappings, StringComparer.OrdinalIgnoreCase),
            ChannelVisibility = new Dictionary<string, bool>(
                _viewerSettings.ChannelVisibility, StringComparer.OrdinalIgnoreCase),
            ChannelOrder = [.. _viewerSettings.ChannelOrder],
            TrackMappings = mappings,
            SessionLayoutAssignments = new Dictionary<string, string>(
                _viewerSettings.SessionLayoutAssignments, StringComparer.OrdinalIgnoreCase)
        };
        var settingsPath = GetViewerSettingsPath();
        try
        {
            var parentDirectory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            File.WriteAllText(settingsPath, JsonSerializer.Serialize(updated, JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show(this,
                $"The track choice could not be saved to {settingsPath}.{Environment.NewLine}{exception.Message}",
                "Could not save track", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _viewerSettings = updated;
        BeginLoading($"Re-cutting “{choice.Venue}” laps");
        try
        {
            var reloaded = await Task.Run(() => venueFiles
                .Select(file => TelemetryLoader.ReadAimCsvSessionInfo(file, _ => shiftSeconds))
                .ToArray());
            foreach (var session in reloaded)
            {
                _csvOriginals[session.FilePath] = session;
                var index = _sessions.FindIndex(item => item.FilePath.Equals(session.FilePath, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    _sessions[index] = session;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "Could not reload CSV", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndLoading();
        }

        _primaryLap = null;
        _referenceLap = null;
        _primaryTrace = null;
        _referenceTrace = null;
        BuildTrackLocations();
        BuildSessionTree();
        StatusText.Text = choice.Target is null
            ? $"“{choice.Venue}” now uses its own track."
            : $"“{choice.Venue}” assigned to {choice.Target.DisplayName} and aligned to its start/finish line.";
    }

    private static AimCsvTransform? FitCsvTransform(IReadOnlyList<string> venueFiles, TrackLocation target)
    {
        if (venueFiles.Count == 0)
        {
            return null;
        }

        var referencePoints = ReadReferenceGpsLap(target);
        if (referencePoints is null)
        {
            return null;
        }

        var unshifted = TelemetryLoader.ReadAimCsvSessionInfo(venueFiles[0]);
        var (shiftSeconds, mirror) = AimCsvAlignment.FindStartShift(
            MedianLap(unshifted).Points, referencePoints);
        var recut = TelemetryLoader.ReadAimCsvSessionInfo(venueFiles[0], _ => shiftSeconds);
        var transform = AimCsvAlignment.FitTransform(MedianLap(recut).Points, referencePoints, shiftSeconds, mirror);
        for (var pass = 0; pass < 2; pass++)
        {
            var correction = AimCsvAlignment.SpeedCorrectionSeconds(
                AimCsvAlignment.Apply(MedianLap(recut).Points, transform), referencePoints);
            if (Math.Abs(correction) < 0.05)
            {
                break;
            }

            shiftSeconds += correction;
            recut = TelemetryLoader.ReadAimCsvSessionInfo(venueFiles[0], _ => shiftSeconds);
            transform = AimCsvAlignment.FitTransform(MedianLap(recut).Points, referencePoints, shiftSeconds, mirror);
        }

        return transform;
    }

    private static AiMLapData MedianLap(SessionInfo session) =>
        session.AiMData!.Laps.OrderBy(lap => lap.DurationMs).ElementAt(session.AiMData.Laps.Count / 2);

    private static IReadOnlyList<TelemetryPoint>? ReadReferenceGpsLap(TrackLocation target)
    {
        foreach (var session in target.Sessions.Where(session => !IsCsvSession(session)).Take(5))
        {
            try
            {
                var lap = TelemetryLoader.ReadLaps(session)
                    .Where(candidate => !candidate.IsAverage && double.IsFinite(candidate.DurationMs) && candidate.DurationMs > 0)
                    .MinBy(candidate => candidate.DurationMs);
                if (lap is null)
                {
                    continue;
                }

                var points = TelemetryLoader.ReadTrace(lap).Points;
                if (points.Count(point => double.IsFinite(point.Latitude) && double.IsFinite(point.Longitude)) > 20)
                {
                    return points;
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
            {
            }
        }

        return null;
    }
}
