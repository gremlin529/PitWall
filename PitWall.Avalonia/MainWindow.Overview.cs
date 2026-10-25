using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Threading;
using TrackTelemetryViewer;

namespace PitWallApp;

// Track overview page, ported from the WPF ShowTrackOverviewAsync / UpdateTrackStatistics flow.
public sealed partial class MainWindow
{
    private readonly ConcurrentDictionary<string, int> _lapCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<TrackPoint>> _meanlineCache = new(StringComparer.OrdinalIgnoreCase);
    private int _overviewGeneration;
    private TrackLocation? _overviewLocation;
    private IReadOnlyList<SessionInfo> _overviewSessions = [];
    private string? _overviewGroup;

    private string FormatTrackLength(double distanceM) => Imperial
        ? $"{distanceM / 1609.344:0.00} mi"
        : $"{distanceM / 1000:0.00} km";

    private static string FormatLapTime(double durationMs)
    {
        var time = TimeSpan.FromMilliseconds(durationMs);
        return $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    private void ApplySessionHeader(TreeNode node, SessionInfo session)
    {
        node.Text = $"{session.SessionLabel}  ·  {_model.GetSessionEdits(session).RaceType}";
        if (_fastestLapMs.TryGetValue(session.FilePath, out var fastest))
        {
            node.Suffix = $"  ({FormatLapTime(fastest)})";
            node.SuffixForeground = Palette.Fastest;
        }

        node.Tail = _model.IsWet(session) ? "  (wet)" : string.Empty;
    }

    private void ApplyDateHeader(TreeNode node, DateTime date)
    {
        node.Text = date.ToString("ddd, MMM d, yyyy");
        var fastest = (node.DateSessions ?? [])
            .Where(session => _fastestLapMs.ContainsKey(session.FilePath))
            .Select(session => _fastestLapMs[session.FilePath])
            .DefaultIfEmpty(double.PositiveInfinity)
            .Min();
        node.Suffix = double.IsFinite(fastest) ? $"  ·  Fastest {FormatLapTime(fastest)}" : string.Empty;
        node.SuffixForeground = Palette.Fastest;
    }

    private void ShowDataPanel()
    {
        _overviewGeneration++;
        Overview.IsVisible = false;
        DataPanel.IsVisible = true;
    }

    private void ResetOverview()
    {
        _overviewGeneration++;
        _overviewLocation = null;
        Overview.IsVisible = false;
        DataPanel.IsVisible = true;
    }

    private void UpdateOverviewLabels()
    {
        if (_overviewLocation is not { } location || !Overview.IsVisible)
        {
            return;
        }

        var length = location.LengthM > 0 ? FormatTrackLength(location.LengthM) : null;
        var visibleCount = location.Sessions.Count(session => !_model.IsVehicleHidden(_model.GetVehicleName(session)));
        Overview.SetHeader(
            _overviewGroup is null ? location.DisplayName : $"{location.DisplayName}  /  {_overviewGroup}",
            $"{location.DisplayName}{(length is null ? string.Empty : $" · {length}")} · {visibleCount} sessions at " +
            FormattableString.Invariant($"{location.Latitude:F5}, {location.Longitude:F5}"));
        Overview.SetLength(length ?? "—");
    }

    private async Task ShowTrackOverviewAsync(
        TrackLocation location, IReadOnlyList<SessionInfo>? scope, string? groupName)
    {
        var generation = ++_overviewGeneration;
        DataPanel.IsVisible = false;
        Overview.IsVisible = true;
        var sessions = (scope ?? location.Sessions)
            .Where(session => !_model.IsVehicleHidden(_model.GetVehicleName(session)))
            .ToArray();
        _overviewLocation = location;
        _overviewSessions = sessions;
        _overviewGroup = groupName;
        UpdateOverviewLabels();
        Overview.SetDrivers([]);
        Overview.SetMeanline([]);
        Overview.SetStats(
            sessions.Select(_model.GetVehicleName).Distinct(StringComparer.CurrentCultureIgnoreCase).Count().ToString("N0"),
            sessions.Length.ToString("N0"), "Counting…",
            location.LengthM > 0 ? FormatTrackLength(location.LengthM) : "—",
            "Loading the reference line and session statistics…");
        StatusText.Text = $"Loading {location.DisplayName}…";
        BeginLoading($"Loading {location.DisplayName}");

        try
        {
            IReadOnlyList<TrackPoint> meanline = [];
            if (location.MeanlinePath is { } path)
            {
                if (!_meanlineCache.TryGetValue(path, out meanline!))
                {
                    try
                    {
                        meanline = await Task.Run(() => TelemetryLoader.ReadMeanlinePoints(path));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                       JsonException or InvalidDataException)
                    {
                        meanline = [];
                    }

                    _meanlineCache[path] = meanline;
                }
            }

            if (generation != _overviewGeneration)
            {
                return;
            }

            Overview.SetMeanline(meanline);
            var finished = false;
            var progress = new Progress<int>(completed =>
            {
                if (generation == _overviewGeneration && !finished)
                {
                    var detail = $"Counting laps from session files… {completed:N0}/{location.Sessions.Count:N0}";
                    Overview.SetStatus(detail);
                    UpdateLoadingDetail($"{location.DisplayName}: {detail}");
                }
            });
            var failures = await CountLapsAsync(location.Sessions, progress);
            finished = true;
            if (generation != _overviewGeneration)
            {
                return;
            }

            UpdateTrackStatistics();
            StatusText.Text = failures == 0
                ? $"Showing track overview for {location.DisplayName}."
                : $"Showing track overview for {location.DisplayName}; {failures} sessions could not be counted.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           JsonException or InvalidDataException or ArgumentException)
        {
            if (generation == _overviewGeneration)
            {
                Overview.SetStats("—", "—", "Unavailable", "—", "Track data could not be fully loaded.");
                StatusText.Text = $"Could not load track overview: {exception.Message}";
            }
        }
        finally
        {
            EndLoading();
        }
    }

    // Counts are cached per session file, so switching between track/car/driver scopes is instant.
    private Task<int> CountLapsAsync(IReadOnlyList<SessionInfo> sessions, IProgress<int> progress) =>
        Task.Run(() =>
        {
            var completed = 0;
            var failures = 0;
            foreach (var session in sessions)
            {
                if (!_lapCounts.ContainsKey(session.FilePath))
                {
                    try
                    {
                        _lapCounts[session.FilePath] =
                            session.AiMData?.Laps.Count ?? TelemetryLoader.ReadLapCount(session.FilePath);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                       JsonException or InvalidDataException or ArgumentException)
                    {
                        failures++;
                    }
                }

                progress.Report(++completed);
            }

            return failures;
        });

    private void UpdateTrackStatistics()
    {
        if (_overviewLocation is not { } location)
        {
            return;
        }

        var sessions = _overviewSessions;
        int LapsOf(SessionInfo session) => _lapCounts.TryGetValue(session.FilePath, out var count) ? count : 0;
        var countingDone = location.Sessions.All(session => _lapCounts.ContainsKey(session.FilePath));
        var vehicleCount = sessions.Select(_model.GetVehicleName).Distinct(StringComparer.CurrentCultureIgnoreCase).Count();
        var rows = sessions.GroupBy(_model.GetDriverName, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(driverGroup =>
            {
                var cars = driverGroup.GroupBy(_model.GetVehicleName, StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(carGroup =>
                    {
                        var carSessions = carGroup.ToArray();
                        var fastest = carSessions.Where(session => _fastestLapMs.ContainsKey(session.FilePath))
                            .MinBy(session => _fastestLapMs[session.FilePath]);
                        var optimal = carSessions.Select(TelemetryLoader.CreateOptimalLap).OfType<LapInfo>()
                            .Where(lap => double.IsFinite(lap.DurationMs) && lap.DurationMs > 0)
                            .MinBy(lap => lap.DurationMs);
                        return new TrackCarStats(
                            carGroup.Key, carSessions.Length,
                            fastest is null ? null : new LapNavigationTarget(fastest, _fastestLapMs[fastest.FilePath], false),
                            optimal is null ? null : new LapNavigationTarget(optimal.Session, optimal.DurationMs, true));
                    })
                    .ToArray();
                return new TrackDriverStats(driverGroup.Key, driverGroup.Sum(LapsOf).ToString("N0"), cars);
            })
            .ToArray();
        Overview.SetDrivers(rows);
        Overview.SetStats(
            vehicleCount.ToString("N0"), sessions.Count.ToString("N0"),
            countingDone || sessions.All(session => _lapCounts.ContainsKey(session.FilePath))
                ? sessions.Sum(LapsOf).ToString("N0")
                : "Counting…",
            location.LengthM > 0 ? FormatTrackLength(location.LengthM) : "—",
            $"{location.DisplayName} · {sessions.Count:N0} sessions · {vehicleCount:N0} vehicles");
    }

    private void StartFastestLapLoad()
    {
        if (_fastestRunning)
        {
            return;
        }

        var generation = _fastestGeneration;
        var pending = _model.Sessions.Where(session => !_fastestLapMs.ContainsKey(session.FilePath)).ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        _fastestRunning = true;
        _ = Task.Run(() =>
        {
            var batch = new List<(SessionInfo Session, double DurationMs)>();
            var failures = 0;
            var clock = Stopwatch.StartNew();

            void Flush(bool final)
            {
                var items = batch.ToArray();
                batch.Clear();
                var failed = failures;
                Dispatcher.UIThread.Post(() => ApplyFastestLaps(generation, items, failed, final));
                clock.Restart();
            }

            foreach (var session in pending)
            {
                if (_closing || generation != _fastestGeneration)
                {
                    break;
                }

                try
                {
                    var duration = session.AiMData is { } aim
                        ? aim.Laps.MinBy(lap => lap.DurationMs)?.DurationMs
                        : TelemetryLoader.ReadFastestLapDurationMs(session.FilePath);
                    if (duration is double value && double.IsFinite(value) && value > 0)
                    {
                        batch.Add((session, value));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                   JsonException or InvalidDataException or ArgumentException)
                {
                    failures++;
                }

                if (clock.ElapsedMilliseconds > 250)
                {
                    Flush(false);
                }
            }

            Flush(true);
        });
    }

    private void ApplyFastestLaps(
        int generation, IReadOnlyList<(SessionInfo Session, double DurationMs)> items, int failures, bool final)
    {
        if (final)
        {
            _fastestRunning = false;
        }

        if (generation != _fastestGeneration)
        {
            if (final)
            {
                StartFastestLapLoad();
            }

            return;
        }

        foreach (var (session, duration) in items)
        {
            _fastestLapMs[session.FilePath] = duration;
            if (_sessionNodes.TryGetValue(session.FilePath, out var node))
            {
                ApplySessionHeader(node, session);
            }

            if (_dateNodes.TryGetValue(session.FilePath, out var dateNode))
            {
                ApplyDateHeader(dateNode, session.LocalStart.Date);
            }
        }

        if (Overview.IsVisible && items.Count > 0 &&
            items.Any(item => _overviewSessions.Any(session => session.FilePath == item.Session.FilePath)))
        {
            UpdateTrackStatistics();
        }

        if (final && failures > 0)
        {
            StatusText.Text = $"Fastest-lap times could not be read for {failures} sessions.";
        }
    }

    private async void NavigateToLap(LapNavigationTarget target)
    {
        if (!_sessionNodes.TryGetValue(target.Session.FilePath, out var sessionNode) ||
            FindPath(node => node == sessionNode) is not { } route)
        {
            StatusText.Text = "The source session is no longer in the current library.";
            return;
        }

        foreach (var node in route)
        {
            node.IsExpanded = true;
        }

        await sessionNode.LoadTask;
        var lapNodes = sessionNode.Children.Where(child => child.Lap is not null).ToArray();
        var lapNode = target.IsOptimal
            ? lapNodes.FirstOrDefault(child => child.Lap!.IsOptimal)
            : lapNodes.Where(child => child.Lap is { IsOptimal: false, IsAverage: false })
                .MinBy(child => child.Lap!.DurationMs);
        if (lapNode is null)
        {
            StatusText.Text = "No matching lap telemetry was found for this session.";
            return;
        }

        SelectNode(node => node == lapNode);
    }
}
