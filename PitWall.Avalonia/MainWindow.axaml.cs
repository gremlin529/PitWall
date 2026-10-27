using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TrackTelemetryViewer;

namespace PitWallApp;

public sealed partial class MainWindow : Window
{
    private const double MpsToKph = 3.6;
    private const double MpsToMph = 2.2369362920544;

    private static readonly IBrush OptimalBrush = Palette.Optimal;
    private static readonly IBrush FastestBrush = Palette.Fastest;
    private static readonly IBrush WetBrush = Palette.Wet;

    private static readonly string LastFolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PitWall", "last-data-folder.txt");

    private SessionModel _model = new(
        [], new ViewerSettings(), new VehicleCatalog([], []),
        new Dictionary<string, string>(), new Dictionary<string, SessionEdits>());
    private List<TreeNode> _roots = [];
    private string? _dataFolder;
    private SessionTrace? _primary;
    private SessionTrace? _reference;
    private bool _loading;
    private IReadOnlyList<AiMChannelData> _aimChannels = [];
    private readonly Dictionary<string, double> _fastestLapMs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TreeNode> _sessionNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TreeNode> _dateNodes = new(StringComparer.OrdinalIgnoreCase);
    private int _fastestGeneration;
    private bool _fastestRunning;
    private int _rebuildGeneration;
    private int _lapGeneration;
    private bool _restoring;

    public MainWindow()
    {
        InitializeComponent();
        EnsureHelperExecutable();
        OpenButton.Click += async (_, _) => await PickFolderAsync();
        UnitsBox.SelectionChanged += (_, _) => OnUnitsChanged();
        Closing += (_, _) => _closing = true;
        Trace.CursorChanged += OnCursorChanged;
        Overview.LapLinkClicked += NavigateToLap;
        SessionTree.SelectionChanged += (_, _) => OnTreeSelectionChanged();
        UpdateLapCards();
        SetReferenceButton.Click += async (_, _) =>
        {
            if (SelectedLap is { } lap)
            {
                await SetReferenceLapAsync(lap);
            }
        };
        SettingsButton.Click += async (_, _) => await ShowSettingsAsync();
        SessionTree.AddHandler(ContextRequestedEvent, OnTreeContextRequested, RoutingStrategies.Bubble);
        ClearReferenceButton.Click += (_, _) =>
        {
            _reference = null;
            ClearReferenceButton.IsEnabled = false;
            RefreshChart();
        };
        Opened += async (_, _) => await LoadInitialFolderAsync();
    }

    private bool Imperial => UnitsBox.SelectedIndex == 1;

    private static void EnsureHelperExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var helper = Path.Combine(AppContext.BaseDirectory, "XrkReader");
        try
        {
            if (File.Exists(helper))
            {
                File.SetUnixFileMode(helper, File.GetUnixFileMode(helper) |
                    UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A read-only install location is fine if the executable bit is already set.
        }
    }

    private async Task LoadInitialFolderAsync()
    {
        var candidates = new List<string>();
        try
        {
            if (File.Exists(LastFolderPath))
            {
                candidates.Add(File.ReadAllText(LastFolderPath).Trim());
            }
        }
        catch (IOException)
        {
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "Data"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "Data"));
        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate) && TelemetryLoader.HasSessionFiles(candidate))
            {
                await LoadFolderAsync(candidate);
                return;
            }
        }
    }

    private async Task PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the data folder",
            AllowMultiple = false
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            await LoadFolderAsync(path);
        }
    }

    private async Task LoadFolderAsync(string folder)
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        OpenButton.IsEnabled = false;
        StatusText.Text = $"Scanning {folder}…";
        BeginLoading("Scanning session files…");
        try
        {
            var scanClock = Stopwatch.StartNew();
            void Report(string detail)
            {
                if (scanClock.ElapsedMilliseconds < 100)
                {
                    return;
                }

                scanClock.Restart();
                Dispatcher.UIThread.Post(() => UpdateLoadingDetail(detail));
            }

            var result = await Task.Run(() => ScanFolder(folder, Report));
            _dataFolder = folder;
            var warnings = new List<string>(result.Failures);
            var settings = SettingsStore.Load(folder, warnings);
            var edits = SettingsStore.LoadEdits(warnings);
            _model = new SessionModel(
                result.Sessions.OrderBy(session => session.LocalStart).ToArray(),
                settings, result.Catalog, result.DriverNames, edits);
            _loading = true;
            UnitsBox.SelectedIndex = settings.DistanceUnits.Equals("Imperial", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;
            _loading = false;
            _aimChannels = ChannelCatalog.DiscoverAim(_model.Sessions);
            _fastestGeneration++;
            _fastestLapMs.Clear();
            _meanlineCache.Clear();
            _lapCounts.Clear();
            _lapGeneration++;
            _primary = _reference = null;
            ClearReferenceButton.IsEnabled = false;
            FolderText.Text = folder;
            SessionCountText.Text = $"{_model.Sessions.Count} sessions";
            UpdateLoadingDetail("Building session library…");
            await RebuildTreeAsync(false);
            RefreshChart();
            var sessionCount = _model.Sessions.Count;
            StatusText.Text = warnings.Count == 0
                ? $"Loaded {sessionCount} sessions across {_model.Locations.Count} tracks."
                : $"Loaded {sessionCount} sessions across {_model.Locations.Count} tracks; " +
                  $"{warnings.Count} warnings. {warnings[0]}";
            if (sessionCount > 0)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LastFolderPath)!);
                    File.WriteAllText(LastFolderPath, folder);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not read folder: {exception.Message}";
        }
        finally
        {
            EndLoading();
            _loading = false;
            OpenButton.IsEnabled = true;
        }
    }

    private sealed record ScanResult(
        List<SessionInfo> Sessions, VehicleCatalog Catalog, IReadOnlyDictionary<string, string> DriverNames,
        List<string> Failures);

    private static ScanResult ScanFolder(string folder, Action<string> report)
    {
        report("Finding telemetry files…");
        var files = TelemetryLoader.EnumerateFilesRecursively(folder, "autosport-performance-*.json")
            .Concat(TelemetryLoader.EnumerateFilesRecursively(folder, "*.xrk")).ToArray();
        var index = TelemetryLoader.BuildDataFileIndex(folder);
        List<SessionInfo> sessions = [];
        List<string> failures = [];
        foreach (var file in files)
        {
            report($"Reading {Path.GetFileName(file)}");
            try
            {
                sessions.Add(Path.GetExtension(file).Equals(".xrk", StringComparison.OrdinalIgnoreCase)
                    ? TelemetryLoader.ReadAiMSessionInfo(file)
                    : TelemetryLoader.ReadSessionInfo(file, index));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidDataException or ArgumentOutOfRangeException or
                                               JsonException or InvalidOperationException or
                                               System.ComponentModel.Win32Exception)
            {
                failures.Add($"{Path.GetFileName(file)}: {exception.Message}");
            }
        }

        VehicleCatalog catalog;
        try
        {
            catalog = TelemetryLoader.ReadVehicleCatalog(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           JsonException or InvalidDataException)
        {
            catalog = new VehicleCatalog([], []);
            failures.Add($"Vehicle catalog: {exception.Message}");
        }

        IReadOnlyDictionary<string, string> driverNames;
        try
        {
            driverNames = TelemetryLoader.ReadDriverNames(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           JsonException or InvalidDataException)
        {
            driverNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            failures.Add($"Driver profiles: {exception.Message}");
        }

        return new ScanResult(sessions, catalog, driverNames, failures);
    }

    private void OnUnitsChanged()
    {
        if (_loading || _dataFolder is null)
        {
            RefreshChart();
            return;
        }

        UpdateOverviewLabels();

        var updated = SettingsStore.Clone(_model.Settings);
        updated.DistanceUnits = Imperial ? "Imperial" : "Metric";
        if (SettingsStore.Save(_dataFolder, updated) is { } error)
        {
            StatusText.Text = error;
        }
        else
        {
            _model.Settings = updated;
        }

        RefreshChart();
    }

    private void BuildTree(bool resetOverview)
    {
        var roots = new List<TreeNode>();
        _sessionNodes.Clear();
        _dateNodes.Clear();
        if (resetOverview)
        {
            ResetOverview();
        }

        foreach (var location in _model.Locations
                     .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var visible = location.Sessions.Where(session => !_model.IsVehicleHidden(_model.GetVehicleName(session)))
                .ToArray();
            if (visible.Length == 0)
            {
                continue;
            }

            var trackNode = new TreeNode($"{location.DisplayName}  ({visible.Length} sessions)")
            {
                Location = location,
                Key = $"L:{location.Key}"
            };
            foreach (var car in visible.GroupBy(_model.GetVehicleName, StringComparer.CurrentCultureIgnoreCase)
                         .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                var carSessions = car.OrderBy(session => session.LocalStart).ToArray();
                var carNode = new TreeNode($"{car.Key}  ({carSessions.Length})")
                {
                    Car = new TrackCarGroup(car.Key, carSessions),
                    Key = $"{trackNode.Key}|C:{car.Key}",
                    Foreground = Palette.Muted
                };
                foreach (var driver in carSessions.GroupBy(_model.GetDriverName, StringComparer.CurrentCultureIgnoreCase)
                             .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase))
                {
                    var driverSessions = driver.ToArray();
                    var driverNode = new TreeNode($"{driver.Key}  ({driverSessions.Length})")
                    {
                        Driver = new TrackDriverGroup(driver.Key, driverSessions),
                        Key = $"{carNode.Key}|D:{driver.Key}",
                        Foreground = Palette.Muted
                    };
                    var optimal = driverSessions.Select(TelemetryLoader.CreateOptimalLap).OfType<LapInfo>()
                        .Where(lap => double.IsFinite(lap.DurationMs) && lap.DurationMs > 0)
                        .MinBy(lap => lap.DurationMs);
                    if (optimal is not null)
                    {
                        driverNode.Suffix = $"  ·  Optimal {FormatLapTime(optimal.DurationMs)}";
                        driverNode.SuffixForeground = OptimalBrush;
                        driverNode.Children.Add(new TreeNode($"Optimal · {optimal.TreeLabel}")
                        {
                            Lap = optimal,
                            Key = $"{driverNode.Key}|optimal",
                            Foreground = OptimalBrush
                        });
                    }

                    foreach (var date in driverSessions.GroupBy(session => session.LocalStart.Date)
                                 .OrderBy(group => group.Key))
                    {
                        var dateSessions = date.OrderBy(session => session.LocalStart).ToArray();
                        var dateNode = new TreeNode(string.Empty)
                        {
                            Key = $"{driverNode.Key}|T:{date.Key:yyyy-MM-dd}",
                            DateSessions = dateSessions,
                            Foreground = Palette.Muted
                        };
                        ApplyDateHeader(dateNode, date.Key);
                        foreach (var session in dateSessions)
                        {
                            _dateNodes[session.FilePath] = dateNode;
                            dateNode.Children.Add(CreateSessionNode(session, dateNode.Key));
                        }

                        driverNode.Children.Add(dateNode);
                    }

                    carNode.Children.Add(driverNode);
                }

                trackNode.Children.Add(carNode);
            }

            roots.Add(trackNode);
        }

        _roots = roots;
        SessionTree.ItemsSource = roots;
        StartFastestLapLoad();
    }

    private TreeNode CreateSessionNode(SessionInfo session, string parentKey)
    {
        var node = new TreeNode(string.Empty)
        {
            Session = session,
            Key = $"S:{session.FilePath}",
            Foreground = _model.IsWet(session) ? WetBrush : Palette.Text
        };
        ApplySessionHeader(node, session);
        _sessionNodes[session.FilePath] = node;
        node.Children.Add(new TreeNode("Loading laps…") { Foreground = Palette.Muted });
        node.LoadChildren = LoadLapsAsync;
        return node;
    }

    private async Task LoadLapsAsync(TreeNode node)
    {
        var session = node.Session!;
        BeginLoading($"Reading laps from {session.FileName}");
        try
        {
            var (laps, optimal) = await Task.Run(() =>
                (TelemetryLoader.ReadLaps(session), TelemetryLoader.CreateOptimalLap(session)));
            var fastest = laps.Where(lap => !lap.IsAverage).MinBy(lap => lap.DurationMs);
            node.Children.Clear();
            if (optimal is not null)
            {
                node.Children.Add(new TreeNode(optimal.TreeLabel)
                {
                    Lap = optimal,
                    Key = $"P:{session.FilePath}|{optimal.TreeLabel}",
                    Foreground = OptimalBrush
                });
            }

            foreach (var lap in laps)
            {
                node.Children.Add(new TreeNode(lap.TreeLabel)
                {
                    Lap = lap,
                    Key = $"P:{session.FilePath}|{lap.TreeLabel}",
                    Foreground = lap == fastest ? FastestBrush : Palette.Text
                });
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or JsonException)
        {
            node.Children.Clear();
            node.Children.Add(new TreeNode($"Could not read laps: {exception.Message}"));
        }
        finally
        {
            EndLoading();
        }
    }

    private LapInfo? SelectedLap => (SessionTree.SelectedItem as TreeNode)?.Lap;

    private async void OnTreeSelectionChanged()
    {
        if (_restoring)
        {
            return;
        }

        SetReferenceButton.IsEnabled = SelectedLap is not null;
        await HandleSelectionAsync();
    }

    private async Task HandleSelectionAsync()
    {
        if (SessionTree.SelectedItem is not TreeNode node)
        {
            return;
        }

        if (node.Lap is { } selectedLap)
        {
            await SelectLapAsync(selectedLap);
        }
        else if (node.Session is { } session)
        {
            await SelectSessionAsync(node, session);
        }
        else if (node.Driver is { } driver)
        {
            if (driver.Sessions.Select(TelemetryLoader.CreateOptimalLap).OfType<LapInfo>()
                    .Where(lap => double.IsFinite(lap.DurationMs) && lap.DurationMs > 0)
                    .MinBy(lap => lap.DurationMs) is { } optimal)
            {
                await SelectLapAsync(optimal);
            }
            else if (_model.LocationOf(driver.Sessions[0]) is { } driverLocation)
            {
                await ShowTrackOverviewAsync(driverLocation, driver.Sessions, driver.Name);
            }
        }
        else if (node.Car is { } car)
        {
            if (_model.LocationOf(car.Sessions[0]) is { } carLocation)
            {
                await ShowTrackOverviewAsync(carLocation, car.Sessions, car.Name);
            }
        }
        else if (node.Location is { } location)
        {
            await ShowTrackOverviewAsync(location, null, null);
        }
    }

    // Like WPF, picking a session opens its fastest lap.
    private async Task SelectSessionAsync(TreeNode node, SessionInfo session)
    {
        node.IsExpanded = true;
        await node.LoadTask;
        var best = node.Children.Where(child => child.Lap is { IsOptimal: false })
            .MinBy(child => child.Lap!.DurationMs);
        if (best is not null)
        {
            Dispatcher.UIThread.Post(() => SessionTree.SelectedItem = best);
        }
        else
        {
            StatusText.Text = $"{session.FileName}: no laps found.";
        }
    }

    private async Task SelectLapAsync(LapInfo lap)
    {
        ShowDataPanel();
        var generation = ++_lapGeneration;
        if (_reference is not null && _model.LocationOf(_reference.Session) != _model.LocationOf(lap.Session))
        {
            _reference = null;
            ClearReferenceButton.IsEnabled = false;
        }

        BeginLoading($"Loading {lap.Session.FileName} · {lap.Label}");
        try
        {
            var trace = await Task.Run(() => TelemetryLoader.ReadTrace(lap));
            if (generation != _lapGeneration)
            {
                return;
            }

            _primary = trace;
            StatusText.Text = $"{lap.Session.FileName}: {lap.Label}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or JsonException)
        {
            if (generation != _lapGeneration)
            {
                return;
            }

            _primary = null;
            StatusText.Text = $"Could not read lap: {exception.Message}";
        }
        finally
        {
            EndLoading();
        }

        RefreshChart();
    }

    private void RefreshChart()
    {
        if (_loading)
        {
            return;
        }

        if (_primary is null)
        {
            Trace.SetData(null, null, []);
            UpdateLapCards();
            return;
        }

        var speedFactor = Imperial ? MpsToMph : MpsToKph;
        var reference = _reference;
        List<ChartSeries> Both(Func<SessionTrace, IReadOnlyList<ChartValue>> select, bool dashed = false)
        {
            var series = new List<ChartSeries> { new(select(_primary), false, dashed) };
            if (reference is not null)
            {
                series.Add(new ChartSeries(select(reference), true, dashed));
            }

            return series;
        }

        var panels = new List<ChartPanel>();
        foreach (var channel in ChannelCatalog.Build(_aimChannels, _model.Settings).Where(item => item.IsEnabled))
        {
            var title = ChannelCatalog.PanelTitle(channel);
            switch (channel.Key)
            {
                case "speed":
                    panels.Add(new ChartPanel(title, Imperial ? "mph" : "km/h",
                        Both(trace => Values(trace, point => point.SpeedMps * speedFactor)), false));
                    break;
                case "combined-g":
                    panels.Add(new ChartPanel(title, "g",
                        [.. Both(trace => Values(trace, point => point.LateralG)),
                         .. Both(trace => Values(trace, point => point.LongitudinalG), true)], true));
                    break;
                case "longitudinal-g":
                    panels.Add(new ChartPanel(title, "g",
                        Both(trace => Values(trace, point => point.LongitudinalG)), true));
                    break;
                case "lateral-g":
                    panels.Add(new ChartPanel(title, "g",
                        Both(trace => Values(trace, point => point.LateralG)), true));
                    break;
                case "delta-time":
                    if (reference is not null)
                    {
                        panels.Add(new ChartPanel(title, "s",
                            [new ChartSeries(DeltaValues(_primary, reference), false)], true));
                    }

                    break;
                default:
                    if (channel.Key.StartsWith(ChannelCatalog.AimPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var name = channel.Key[ChannelCatalog.AimPrefix.Length..];
                        List<ChartSeries> series = [new(_primary.GetChannelValues(name), false)];
                        if (reference?.Session.AiMData is not null)
                        {
                            series.Add(new ChartSeries(reference.GetChannelValues(name), true));
                        }

                        panels.Add(new ChartPanel(title, channel.Units, series, false));
                    }

                    break;
            }
        }

        Trace.SetData(_primary, _reference, panels);
        UpdateLapCards();
    }

    private void UpdateLapCards()
    {
        CursorDistanceText.Text = "—";
        if (_primary is null)
        {
            TrackTitleText.Text = "Select a session";
            SessionSubtitleText.Text = "Choose a lap from the session library to begin.";
            PrimaryLapText.Text = ReferenceLapText.Text = LapDeltaText.Text = "—";
            return;
        }

        var session = _primary.Session;
        TrackTitleText.Text = _model.LocationOf(session)?.DisplayName ?? session.FileName;
        SessionSubtitleText.Text =
            $"{_model.GetVehicleName(session)}  ·  {_model.GetDriverName(session)}  ·  {session.SessionLabel}";
        PrimaryLapText.Text = _primary.Lap.ReferenceLabel;
        if (_reference is null)
        {
            ReferenceLapText.Text = LapDeltaText.Text = "—";
            return;
        }

        ReferenceLapText.Text = _reference.Lap.ReferenceLabel;
        var delta = _primary.LapDurationSeconds - _reference.LapDurationSeconds;
        LapDeltaText.Text = $"{(delta > 0 ? "+" : string.Empty)}{delta:0.000} s";
    }

    private void OnCursorChanged(double? distanceM) =>
        CursorDistanceText.Text = distanceM is not { } meters
            ? "—"
            : Imperial ? $"{meters * 3.28084:0} ft" : $"{meters:0} m";

    private static IReadOnlyList<ChartValue> Values(SessionTrace trace, Func<TelemetryPoint, double> select) =>
        trace.Points.Select(point => new ChartValue(point.DistanceM, select(point))).ToArray();

    private static IReadOnlyList<ChartValue> DeltaValues(SessionTrace primary, SessionTrace reference)
    {
        var primaryStart = primary.Points[0].TimeMs;
        var referenceStart = reference.Points[0].TimeMs;
        return primary.Points.Select(point => new ChartValue(
                point.DistanceM,
                point.DistanceM <= reference.DistanceM
                    ? ((point.TimeMs - primaryStart) -
                       (TelemetryLoader.InterpolateTimeMs(reference.Points, point.DistanceM) - referenceStart)) / 1000d
                    : double.NaN))
            .ToArray();
    }
}
