using System.Text.Json;
using Avalonia.Controls;
using TrackTelemetryViewer;

namespace PitWallApp;

// Race Studio CSV track assignment, ported from the WPF MainWindow.CsvTrack.cs.
public sealed partial class MainWindow
{
    private void AddCsvTrackItems(List<Control> items, TrackLocation location)
    {
        var venues = location.Sessions
            .Where(SessionModel.IsCsvSession)
            .Select(session => _model.CsvOriginals.TryGetValue(session.FilePath, out var original) ? original : session)
            .Select(session => session.AiMData!.TrackName.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (venues.Length == 0)
        {
            return;
        }

        var targets = _model.Locations
            .Where(candidate => !ReferenceEquals(candidate, location) &&
                                candidate.Sessions.Any(session => !SessionModel.IsCsvSession(session)))
            .OrderBy(candidate => candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var assignedVenues = venues.Where(venue => SessionModel.GetCsvAssignment(_model.Settings, venue) is not null)
            .ToArray();
        foreach (var venue in venues)
        {
            if (targets.Length > 0)
            {
                var assignMenu = new MenuItem
                {
                    Header = venues.Length > 1 ? $"Assign “{venue}” to track" : "Assign CSV data to track"
                };
                foreach (var target in targets)
                {
                    assignMenu.Items.Add(Item(target.DisplayName, async () => await AssignCsvTrackAsync(venue, target)));
                }

                items.Add(assignMenu);
            }

            if (assignedVenues.Contains(venue, StringComparer.CurrentCultureIgnoreCase))
            {
                items.Add(Item(
                    venues.Length > 1 ? $"Unassign “{venue}” (use its own track)" : "Unassign CSV track",
                    async () => await AssignCsvTrackAsync(venue, null)));
            }
        }
    }

    private async Task AssignCsvTrackAsync(string venue, TrackLocation? target)
    {
        if (_dataFolder is null)
        {
            return;
        }

        var key = SessionModel.CsvVenuePrefixedKey(venue);
        var updated = SettingsStore.Clone(_model.Settings);
        var venueFiles = _model.CsvOriginals.Values
            .Where(session => session.AiMData!.TrackName.Trim().Equals(venue, StringComparison.CurrentCultureIgnoreCase))
            .Select(session => session.FilePath)
            .ToArray();
        double shiftSeconds = 0;
        if (target is null)
        {
            updated.TrackMappings.Remove(key);
        }
        else
        {
            BeginLoading($"Aligning “{venue}” to {target.DisplayName}");
            try
            {
                var transform = await Task.Run(() => FitCsvTransform(venueFiles, target));
                if (transform is null)
                {
                    await Dialogs.ShowMessageAsync(this, "Could not align",
                        "No GPS lap was found at that track to align the CSV data against.");
                    return;
                }

                shiftSeconds = transform.ShiftSeconds;
                var configuration = target.ConfigurationName is "Combined layouts" or SessionModel.LayoutUnspecifiedLabel
                    ? string.Empty
                    : target.ConfigurationName;
                updated.TrackMappings[key] = SessionModel.SerializeCsvAssignment(new CsvVenueAssignment(
                    target.TrackName, configuration, target.Latitude, target.Longitude, transform));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
            {
                await Dialogs.ShowMessageAsync(this, "Could not align", exception.Message);
                return;
            }
            finally
            {
                EndLoading();
            }
        }

        if (!SaveSettings(updated, "Could not save track"))
        {
            return;
        }

        BeginLoading($"Re-cutting “{venue}” laps");
        try
        {
            var reloaded = await Task.Run(() => venueFiles
                .Select(file => TelemetryLoader.ReadAimCsvSessionInfo(file, _ => shiftSeconds))
                .ToArray());
            foreach (var session in reloaded)
            {
                _model.CsvOriginals[session.FilePath] = session;
                _model.ReplaceSession(session);
                _lapsBySession.Remove(session.FilePath);
                _lapCounts.TryRemove(session.FilePath, out _);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(this, "Could not reload CSV", exception.Message);
        }
        finally
        {
            EndLoading();
        }

        _primaryLap = _referenceLap = null;
        _primary = _reference = null;
        ClearReferenceButton.IsEnabled = false;
        _model.BuildTrackLocations();
        SeedLoggerLaps();
        await RebuildTreeAsync(false);
        RefreshChart();
        StatusText.Text = target is null
            ? $"“{venue}” now uses its own track."
            : $"“{venue}” assigned to {target.DisplayName} and aligned to its start/finish line.";
    }

    private static AimCsvTransform? FitCsvTransform(IReadOnlyList<string> venueFiles, TrackLocation target)
    {
        if (venueFiles.Count == 0 || ReadReferenceGpsLap(target) is not { } referencePoints)
        {
            return null;
        }

        var unshifted = TelemetryLoader.ReadAimCsvSessionInfo(venueFiles[0]);
        var (shiftSeconds, mirror) = AimCsvAlignment.FindStartShift(MedianLap(unshifted).Points, referencePoints);
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
        foreach (var session in target.Sessions.Where(session => !SessionModel.IsCsvSession(session)).Take(5))
        {
            try
            {
                var lap = FastestDrivenLap(TelemetryLoader.ReadLaps(session));
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
