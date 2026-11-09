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
    private IReadOnlyList<TrackPoint> _overviewOutline = [];
    private TrackSegmentMap? _overviewSegments;
    private CancellationTokenSource? _elevationCancellation;

    // After a folder loads, refine each track's elevation profile from its sessions in the background.
    private void StartElevationRefinement()
    {
        _elevationCancellation?.Cancel();
        var cancellation = _elevationCancellation = new CancellationTokenSource();
        _ = TrackElevation.RunInBackground(_model.Locations, location =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (cancellation.IsCancellationRequested || _overviewLocation?.Key != location.Key ||
                    !Overview.IsVisible || _overviewOutline.Count == 0)
                {
                    return;
                }

                var outline = _overviewOutline;
                var segments = _overviewSegments;
                var generation = _overviewGeneration;
                _ = Task.Run(() => TrackElevation.Apply(location, outline)).ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully && generation == _overviewGeneration)
                    {
                        Overview.SetMap(task.Result, segments);
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());
            });
        }, progress => Dispatcher.UIThread.Post(() => ShowElevationProgress(progress, cancellation)),
            cancellation.Token);
    }

    private void ShowElevationProgress(ElevationProgress progress, CancellationTokenSource cancellation)
    {
        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        ElevationStatusText.Text = progress.Text;
        ElevationStatusText.IsVisible = true;
        if (progress.Finished)
        {
            _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ =>
            {
                if (_elevationCancellation == cancellation)
                {
                    ElevationStatusText.IsVisible = false;
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    private string FormatTrackLength(double distanceM) => Imperial
        ? $"{distanceM / 1609.344:0.00} mi"
        : $"{distanceM / 1000:0.00} km";

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

        var visibleCount = location.Sessions.Count(session => !_model.IsVehicleHidden(_model.GetVehicleName(session)));
        Overview.SetHeader(
            _overviewGroup is null ? location.DisplayName : $"{location.DisplayName}  /  {_overviewGroup}",
            $"{location.DisplayName} · {FormatTrackLength(location.LengthM)} · {visibleCount} sessions at " +
            FormattableString.Invariant($"{location.Latitude:F5}, {location.Longitude:F5}"));
        Overview.SetLength(location.LengthM > 0 ? FormatTrackLength(location.LengthM) : "—");
    }

    private async Task ShowTrackOverviewAsync(
        TrackLocation location, IReadOnlyList<SessionInfo>? scope, string? groupName)
    {
        var generation = ++_overviewGeneration;
        _primaryLap = null;
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
        Overview.SetStats(
            sessions.Select(_model.GetVehicleName).Distinct(StringComparer.CurrentCultureIgnoreCase).Count().ToString("N0"),
            sessions.Length.ToString("N0"), "Counting…",
            location.LengthM > 0 ? FormatTrackLength(location.LengthM) : "—",
            "Loading the reference line and session statistics…");
        BeginLoading($"Loading {location.DisplayName}");

        try
        {
            IReadOnlyList<TrackPoint> meanline = [];
            if (location.MeanlinePath is { } path)
            {
                if (!_meanlineCache.TryGetValue(path, out meanline!))
                {
                    meanline = await Task.Run(() => TelemetryLoader.ReadMeanlinePoints(path));
                    _meanlineCache[path] = meanline;
                }
            }

            if (meanline.Count == 0)
            {
                // Logger-only venues have no meanline: build a profile from the sessions' own GPS (cached on disk).
                meanline = await Task.Run(() => TrackProfileCache.GetOrBuild(sessions));
            }

            if (generation != _overviewGeneration)
            {
                return;
            }

            var segments = await LoadSegmentMapAsync(location.MeanlinePath);
            if (generation != _overviewGeneration)
            {
                return;
            }

            // Use the refined elevation profile for this track once the background pass has built one.
            var baseOutline = meanline;
            var refined = await Task.Run(() => TrackElevation.Apply(location, baseOutline));
            if (generation != _overviewGeneration)
            {
                return;
            }

            _overviewOutline = baseOutline;
            _overviewSegments = segments;
            Overview.SetMap(refined, segments);
            var finished = false;
            var progress = new Progress<int>(completed =>
            {
                if (generation == _overviewGeneration && !finished)
                {
                    Overview.SetStatus($"Counting laps from session files… {completed:N0}/{location.Sessions.Count:N0}");
                    UpdateLoadingDetail(
                        $"Counting laps · {location.DisplayName} · {completed:N0}/{location.Sessions.Count:N0}");
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
                StatusText.Text = "Could not load track overview.";
                await Dialogs.ShowMessageAsync(this, "Track overview could not be loaded", exception.Message);
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
                        var optimal = BestOptimalLap(carSessions);
                        return new TrackCarStats(
                            carGroup.Key, carSessions.Length,
                            fastest is null ? null : new LapNavigationTarget(fastest, _fastestLapMs[fastest.FilePath], false),
                            optimal is null ? null : new LapNavigationTarget(optimal.Session, optimal.DurationMs, true),
                            carSessions.Sum(LapsOf));
                    })
                    .ToArray();
                return new TrackDriverStats(driverGroup.Key, driverGroup.Sum(LapsOf).ToString("N0"), cars);
            })
            .ToArray();
        Overview.SetDrivers(rows);
        Overview.SetStats(
            vehicleCount.ToString("N0"), sessions.Count.ToString("N0"),
            sessions.All(session => _lapCounts.ContainsKey(session.FilePath))
                ? sessions.Sum(LapsOf).ToString("N0")
                : "Counting…",
            location.LengthM > 0 ? FormatTrackLength(location.LengthM) : "—",
            $"{location.DisplayName} · {sessions.Count:N0} sessions · {vehicleCount:N0} vehicles");
    }

    // Reads every session's fastest lap in the background so date and session headers fill in (WPF LoadFastestLapHeadersAsync).
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
            var failures = new List<string>();
            var clock = Stopwatch.StartNew();

            void Flush(bool final)
            {
                var items = batch.ToArray();
                batch.Clear();
                var failed = failures.ToArray();
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
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    failures.Add($"{session.FileName}: {exception.Message}");
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
        int generation, IReadOnlyList<(SessionInfo Session, double DurationMs)> items, IReadOnlyList<string> failures,
        bool final)
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
            // A lap list that has already been read is authoritative.
            if (!_lapsBySession.ContainsKey(session.FilePath))
            {
                _fastestLapMs[session.FilePath] = duration;
            }
        }

        // Refresh headers only after the whole batch is stored, or later sessions of a driver would be missed.
        var drivers = new HashSet<TreeNode>();
        foreach (var (session, _) in items)
        {
            if (_driverNodes.TryGetValue(session.FilePath, out var driverNode) && drivers.Add(driverNode))
            {
                UpdateDriverHeaders(session);
            }
        }

        if (Overview.IsVisible && items.Count > 0 &&
            items.Any(item => _overviewSessions.Any(session => session.FilePath == item.Session.FilePath)))
        {
            UpdateTrackStatistics();
        }

        if (final && failures.Count > 0)
        {
            _ = Dialogs.ShowMessageAsync(this, "Some fastest-lap times are unavailable",
                $"Fastest-lap summaries could not be read for {failures.Count} sessions. The first error was: {failures[0]}");
        }
    }

    private async void NavigateToLap(LapNavigationTarget target)
    {
        if (!_sessionNodes.TryGetValue(target.Session.FilePath, out var sessionNode) ||
            FindPath(node => node == sessionNode) is not { } route)
        {
            await Dialogs.ShowMessageAsync(this, "Lap is unavailable", "The source session is no longer in the current library.");
            return;
        }

        foreach (var node in route)
        {
            node.IsExpanded = true;
        }

        await sessionNode.EnsureChildrenLoaded();
        var lapNodes = sessionNode.Children.Where(child => child.Lap is not null).ToArray();
        var lapNode = target.IsOptimal
            ? lapNodes.FirstOrDefault(child => child.Lap!.IsOptimal)
            : lapNodes.Where(child => child.Lap is { IsOptimal: false, IsAverage: false })
                .MinBy(child => child.Lap!.DurationMs);
        if (lapNode is null)
        {
            await Dialogs.ShowMessageAsync(this, "Lap is unavailable", "No matching lap telemetry was found for this session.");
            return;
        }

        SelectNode(node => node == lapNode);
    }
}
