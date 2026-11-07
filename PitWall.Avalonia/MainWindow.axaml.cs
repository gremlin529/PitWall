using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TrackTelemetryViewer;

namespace PitWallApp;

public sealed partial class MainWindow : Window
{
    private const double MpsToKph = 3.6;
    private const double MpsToMph = 2.236936292;

    private static readonly string UserFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackTelemetryViewer");
    private static readonly string LastFolderPath = Path.Combine(UserFolder, "last-data-folder.txt");
    private static readonly string LegacyLastFolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PitWall", "last-data-folder.txt");

    private SessionModel _model = new(
        [], new ViewerSettings(), new VehicleCatalog([], []),
        new Dictionary<string, string>(), new Dictionary<string, SessionEdits>());
    private List<TreeNode> _roots = [];
    private string? _dataFolder;
    private LapInfo? _primaryLap;
    private LapInfo? _referenceLap;
    private SessionTrace? _primary;
    private SessionTrace? _reference;
    private TrackSegmentMap? _segmentMap;
    private bool _loading;
    private bool _folderLoading;
    private IReadOnlyList<AiMChannelData> _aimChannels = [];
    private readonly Dictionary<string, double> _fastestLapMs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<LapInfo>> _lapsBySession = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrackSegmentMap?> _segmentCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TreeNode> _sessionNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TreeNode> _driverNodes = new(StringComparer.OrdinalIgnoreCase);
    private int _fastestGeneration;
    private bool _fastestRunning;
    private int _rebuildGeneration;
    private int _lapGeneration;
    private bool _restoring;

    public MainWindow()
    {
        InitializeComponent();
        EnsureHelperExecutable();
        LoadLogo();
        ShowAppVersion();
        RestoreWindowPlacement();
        OpenButton.Click += async (_, _) => await PickFolderAsync();
        ImportButton.Click += async (_, _) => await ImportPackagesAsync();
        UpdateButton.Click += (_, _) => OpenUpdatePage();
        UnitsBox.SelectionChanged += (_, _) => OnUnitsChanged();
        Closing += (_, _) =>
        {
            _closing = true;
            SaveWindowPlacement();
        };
        Trace.CursorChanged += OnCursorChanged;
        Overview.LapLinkClicked += NavigateToLap;
        SessionTree.SelectionChanged += (_, _) => OnTreeSelectionChanged();
        SessionTree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        SessionTree.AddHandler(ContextRequestedEvent, OnTreeContextRequested, RoutingStrategies.Bubble);
        SetReferenceButton.Click += async (_, _) =>
        {
            if (SelectedLap is { } lap)
            {
                await SetReferenceLapAsync(lap);
            }
        };
        ClearReferenceButton.Click += (_, _) => ClearReference();
        SettingsButton.Click += async (_, _) => await ShowSettingsAsync();
        RefreshChart();
        Opened += async (_, _) =>
        {
            _ = CheckForUpdateAsync();
            await LoadInitialFolderAsync();
        };
    }

    private bool Imperial => UnitsBox.SelectedIndex == 1;

    private void LoadLogo()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://PitWall/Assets/PitwallLogo.png"));
            LogoImage.Source = new Bitmap(stream);
        }
        catch (Exception exception) when (exception is IOException or FileNotFoundException or ArgumentException)
        {
            LogoImage.IsVisible = false;
        }
    }

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
        foreach (var path in new[] { LastFolderPath, LegacyLastFolderPath })
        {
            try
            {
                if (File.Exists(path))
                {
                    candidates.Add(File.ReadAllText(path).Trim());
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "Data"));
        candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Data")));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "Data"));
        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate) && TelemetryLoader.HasSessionFiles(candidate))
            {
                await LoadFolderAsync(candidate);
                return;
            }
        }

        StatusText.Text = "Choose the folder containing your session JSON files.";
    }

    private async Task PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the folder containing session JSON files",
            AllowMultiple = false
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            await LoadFolderAsync(path);
        }
    }

    private async Task LoadFolderAsync(string folder, IReadOnlyList<string>? extraPackages = null)
    {
        if (_folderLoading)
        {
            return;
        }

        if (!Directory.Exists(folder))
        {
            await Dialogs.ShowMessageAsync(this, "Folder not found", $"The selected folder does not exist:\n{folder}");
            return;
        }

        _folderLoading = true;
        _loading = true;
        OpenButton.IsEnabled = ImportButton.IsEnabled = false;
        StatusText.Text = "Scanning session files…";
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

            var result = await Task.Run(() => ScanFolder(folder, extraPackages ?? [], Report));
            if (result.FileCount == 0)
            {
                StatusText.Text = "No session files found.";
                await Dialogs.ShowMessageAsync(this, "No session files",
                    "No Garmin performance JSON, AiM XRK/CSV or VBOX session files were found in the selected folder.");
                return;
            }

            _dataFolder = folder;
            var warnings = new List<string>(result.Failures);
            var edits = SettingsStore.LoadEdits(warnings);
            var settings = result.Settings;
            ApplyImportedCorrections(result.Import, settings, edits);
            var alignedSessions = await Task.Run(() => AimGpsSessionAlignment.CorrectSessions(result.Sessions));
            _model = new SessionModel(
                alignedSessions.OrderBy(session => session.LocalStart), settings, result.Catalog, result.DriverNames, edits);
            _loading = true;
            UnitsBox.SelectedIndex = settings.DistanceUnits.Equals("Imperial", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;
            _aimChannels = ChannelCatalog.DiscoverAim(_model.Sessions);
            _fastestGeneration++;
            _fastestLapMs.Clear();
            _lapsBySession.Clear();
            _meanlineCache.Clear();
            _segmentCache.Clear();
            _lapCounts.Clear();
            _lapGeneration++;
            _primaryLap = _referenceLap = null;
            _primary = _reference = null;
            _segmentMap = null;
            ClearReferenceButton.IsEnabled = false;
            SeedLoggerLaps();
            FolderText.Text = folder;
            SessionCountText.Text = $"{_model.Sessions.Count} SESSIONS";
            UpdateLoadingDetail("Building session library…");
            _loading = false;
            await RebuildTreeAsync(false);
            RefreshChart();
            ReportImport(result.Import);

            if (result.Failures.Count > 0)
            {
                var details = string.Join(Environment.NewLine, result.Failures.Take(8));
                if (result.Failures.Count > 8)
                {
                    details += $"{Environment.NewLine}…and {result.Failures.Count - 8} more.";
                }

                _ = Dialogs.ShowMessageAsync(this, "Session scan completed with errors",
                    $"Loaded {_model.Sessions.Count} of {result.FileCount} files. Some files could not be read:" +
                    $"{Environment.NewLine}{details}");
            }

            var sessionCount = _model.Sessions.Count;
            if (sessionCount == 0)
            {
                StatusText.Text = "No valid sessions found.";
                return;
            }

            StatusText.Text = warnings.Count == result.Failures.Count
                ? $"Loaded {sessionCount} sessions across {_model.Locations.Count} tracks."
                : $"Loaded {sessionCount} sessions across {_model.Locations.Count} tracks; " +
                  $"{warnings.Count - result.Failures.Count} settings warnings. {warnings[^1]}";
            SaveLastDataFolder(folder);
            SelectFirstTrack();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not read folder: {exception.Message}";
        }
        finally
        {
            EndLoading();
            _loading = false;
            _folderLoading = false;
            OpenButton.IsEnabled = ImportButton.IsEnabled = true;
        }
    }

    private static void SaveLastDataFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(UserFolder);
            File.WriteAllText(LastFolderPath, folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    // AiM/CSV/VBOX laps are already in memory, so their lap lists and fastest times are known immediately (as in WPF).
    private void SeedLoggerLaps()
    {
        foreach (var session in _model.Sessions.Where(session => session.AiMData is not null))
        {
            var laps = TelemetryLoader.ReadLaps(session);
            _lapsBySession[session.FilePath] = laps;
            if (FastestDrivenLap(laps) is { } fastest)
            {
                _fastestLapMs[session.FilePath] = fastest.DurationMs;
            }
        }
    }

    private sealed record ScanResult(
        List<SessionInfo> Sessions,
        int FileCount,
        ViewerSettings Settings,
        VehicleCatalog Catalog,
        IReadOnlyDictionary<string, string> DriverNames,
        PackageImportResult Import,
        List<string> Failures);

    private static ScanResult ScanFolder(string folder, IReadOnlyList<string> extraPackages, Action<string> report)
    {
        report("Unpacking PitWall packages…");
        var import = ExtractPackages(folder, extraPackages);
        report("Finding telemetry files…");
        List<string> failures = [];
        // Settings are read before the scan so CSV laps are cut with their venue's saved start/finish shift.
        var settings = SettingsStore.Load(folder, failures);
        var files = TelemetryLoader.EnumerateFilesRecursively(folder, "autosport-performance-*.json")
            .Concat(TelemetryLoader.EnumerateAimSessionFiles(folder))
            .Concat(TelemetryLoader.EnumerateAimCsvFiles(folder))
            .Concat(TelemetryLoader.EnumerateVboFiles(folder))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var index = TelemetryLoader.BuildDataFileIndex(folder);
        var results = new SessionInfo?[files.Length];
        var errors = new string?[files.Length];
        var completed = 0;
        var reportLock = new object();
        // AiM files each take a second or more to decode (a separate reader process), so read in parallel.
        Parallel.For(0, files.Length, fileIndex =>
        {
            var file = files[fileIndex];
            try
            {
                var extension = Path.GetExtension(file);
                results[fileIndex] = TelemetryLoader.IsAimSessionFile(file)
                    ? TelemetryLoader.ReadAiMSessionInfo(file)
                    : extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
                        ? TelemetryLoader.ReadAimCsvSessionInfo(file,
                            venue => SessionModel.GetCsvShiftSeconds(settings, venue))
                        : extension.Equals(".vbo", StringComparison.OrdinalIgnoreCase)
                            ? TelemetryLoader.ReadVboSessionInfo(file)
                            : TelemetryLoader.ReadSessionInfo(file, index);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidDataException or ArgumentOutOfRangeException or
                                               JsonException or InvalidOperationException or
                                               System.ComponentModel.Win32Exception)
            {
                errors[fileIndex] = $"{Path.GetFileName(file)}: {exception.Message}";
            }

            var done = Interlocked.Increment(ref completed);
            lock (reportLock)
            {
                report($"Reading session files… {done} of {files.Length}");
            }
        });
        List<SessionInfo> sessions = [.. results.OfType<SessionInfo>()];
        failures.AddRange(errors.OfType<string>());

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

        return new ScanResult(sessions, files.Length, settings, catalog, driverNames, import, failures);
    }

    private void OnUnitsChanged()
    {
        if (_loading || _dataFolder is null)
        {
            RefreshChart();
            return;
        }

        UpdateOverviewLabels();
        var units = Imperial ? "Imperial" : "Metric";
        if (units.Equals(_model.Settings.DistanceUnits, StringComparison.OrdinalIgnoreCase))
        {
            // Avalonia can raise a late SelectionChanged when the box is templated; don't rewrite settings for it.
            RefreshChart();
            return;
        }

        var updated = SettingsStore.Clone(_model.Settings);
        updated.DistanceUnits = units;
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

    // ---- Session library tree (mirrors the WPF BuildSessionTree) ----

    private void BuildTree(bool resetOverview)
    {
        var roots = new List<TreeNode>();
        _sessionNodes.Clear();
        _driverNodes.Clear();
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

            var trackNode = new TreeNode($"{location.DisplayName}  ({visible.Length})")
            {
                Location = location,
                Key = $"L:{location.Key}",
                Weight = FontWeight.SemiBold
            };
            foreach (var car in visible.GroupBy(_model.GetVehicleName, StringComparer.CurrentCultureIgnoreCase)
                         .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                var carSessions = car.OrderBy(session => session.LocalStart).ToArray();
                var carNode = new TreeNode($"{car.Key}  ({carSessions.Length})")
                {
                    Car = new TrackCarGroup(car.Key, carSessions),
                    Key = $"{trackNode.Key}|C:{car.Key}",
                    Foreground = Palette.TreeMuted
                };
                foreach (var driver in carSessions.GroupBy(_model.GetDriverName, StringComparer.CurrentCultureIgnoreCase)
                             .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase))
                {
                    var driverSessions = driver.OrderBy(session => session.LocalStart).ToArray();
                    var optimal = BestOptimalLap(driverSessions);
                    var driverNode = new TreeNode($"{driver.Key}  ({driverSessions.Length})")
                    {
                        Driver = new TrackDriverGroup(driver.Key, driverSessions, optimal),
                        Key = $"{carNode.Key}|D:{driver.Key}",
                        Foreground = Palette.TreeMuted
                    };
                    if (optimal is not null)
                    {
                        driverNode.Suffix = $"  ·  Optimal {FormatLapTime(optimal.DurationMs)}";
                        driverNode.SuffixForeground = Palette.Optimal;
                    }

                    foreach (var date in driverSessions.GroupBy(session => session.LocalStart.Date)
                                 .OrderBy(group => group.Key))
                    {
                        var dateSessions = date.ToArray();
                        var dateNode = new TreeNode(string.Empty)
                        {
                            Key = $"{driverNode.Key}|T:{date.Key:yyyy-MM-dd}",
                            Date = new TrackDateGroup(date.Key, dateSessions),
                            DriverSessions = driverSessions,
                            Foreground = Palette.TreeMuted
                        };
                        ApplyDateHeader(dateNode);
                        foreach (var session in dateSessions)
                        {
                            _driverNodes[session.FilePath] = driverNode;
                            dateNode.Children.Add(CreateSessionNode(session, dateNode));
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

    private TreeNode CreateSessionNode(SessionInfo session, TreeNode dateNode)
    {
        var node = new TreeNode(string.Empty)
        {
            Session = session,
            Date = dateNode.Date,
            DriverSessions = dateNode.DriverSessions,
            Key = $"S:{session.FilePath}"
        };
        ApplySessionHeader(node);
        _sessionNodes[session.FilePath] = node;
        node.Children.Add(new TreeNode("Loading laps…") { Foreground = Palette.TreeMuted });
        node.LoadChildren = LoadLapsAsync;
        return node;
    }

    private void SelectFirstTrack()
    {
        if (_roots.Count > 0)
        {
            SessionTree.SelectedItem = _roots[0];
        }
    }

    private static string FormatLapTime(double durationMs)
    {
        var time = TimeSpan.FromMilliseconds(durationMs);
        return $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    private static LapInfo? FastestDrivenLap(IEnumerable<LapInfo> laps) => laps
        .Where(lap => !lap.IsAverage && !lap.IsOptimal && double.IsFinite(lap.DurationMs) && lap.DurationMs > 0)
        .MinBy(lap => lap.DurationMs);

    private static LapInfo? BestOptimalLap(IEnumerable<SessionInfo> sessions) => sessions
        .Select(TelemetryLoader.CreateOptimalLap)
        .OfType<LapInfo>()
        .Where(lap => double.IsFinite(lap.DurationMs) && lap.DurationMs > 0)
        .MinBy(lap => lap.DurationMs);

    private double GetFastestDuration(IEnumerable<SessionInfo> sessions) => sessions
        .Where(session => _fastestLapMs.ContainsKey(session.FilePath))
        .Select(session => _fastestLapMs[session.FilePath])
        .DefaultIfEmpty(double.PositiveInfinity)
        .Min();

    private bool IsFastestSessionOfDate(SessionInfo session, IEnumerable<SessionInfo> dateSessions)
    {
        var fastestSession = dateSessions
            .Where(candidate => _fastestLapMs.ContainsKey(candidate.FilePath))
            .MinBy(candidate => _fastestLapMs[candidate.FilePath]);
        return fastestSession is not null &&
               fastestSession.FilePath.Equals(session.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    // "Tue, Mar 4, 2025  ·  Fastest 1:58.123" — the time is green only when it is the driver's fastest day.
    private void ApplyDateHeader(TreeNode node)
    {
        var date = node.Date!;
        node.Text = date.Date.ToString("ddd, MMM d, yyyy");
        var fastest = GetFastestDuration(date.Sessions);
        if (!double.IsFinite(fastest))
        {
            node.Suffix = string.Empty;
            node.SuffixForeground = null;
            return;
        }

        node.Suffix = $"  ·  Fastest {FormatLapTime(fastest)}";
        node.SuffixForeground = fastest <= GetFastestDuration(node.DriverSessions ?? date.Sessions)
            ? Palette.Fastest
            : null;
    }

    // "9:41 AM   ·   0:24:13  (1:58.123)  (wet)" — wet sessions are blue; the time is green for the day's fastest
    // session and clicking it loads that lap.
    private void ApplySessionHeader(TreeNode node)
    {
        var session = node.Session!;
        var wet = _model.IsWet(session);
        node.Foreground = wet ? Palette.Wet : Palette.Text;
        node.Text = $"{session.SessionLabel}   ·   {TimeSpan.FromSeconds(session.SessionDurationSeconds):h\\:mm\\:ss}";
        if (_fastestLapMs.TryGetValue(session.FilePath, out var fastest))
        {
            node.Suffix = $"  ({FormatLapTime(fastest)})";
            node.SuffixForeground = node.Date is { } date && IsFastestSessionOfDate(session, date.Sessions)
                ? Palette.Fastest
                : null;
            node.SuffixIsLink = true;
        }
        else
        {
            node.Suffix = string.Empty;
            node.SuffixForeground = null;
            node.SuffixIsLink = false;
        }

        node.Tail = wet ? "  (wet)" : string.Empty;
    }

    // A new fastest time can change which date and session is green, so refresh every date of that driver.
    private void UpdateDriverHeaders(SessionInfo session)
    {
        if (!_driverNodes.TryGetValue(session.FilePath, out var driverNode))
        {
            return;
        }

        foreach (var dateNode in driverNode.Children.Where(child => child.Date is not null))
        {
            ApplyDateHeader(dateNode);
            foreach (var sessionNode in dateNode.Children.Where(child => child.Session is not null))
            {
                ApplySessionHeader(sessionNode);
            }
        }
    }

    private async Task<IReadOnlyList<LapInfo>> GetLapsAsync(SessionInfo session)
    {
        if (_lapsBySession.TryGetValue(session.FilePath, out var laps))
        {
            return laps;
        }

        laps = await Task.Run(() => TelemetryLoader.ReadLaps(session));
        _lapsBySession[session.FilePath] = laps;
        return laps;
    }

    private async Task LoadLapsAsync(TreeNode node)
    {
        var session = node.Session!;
        var cached = _lapsBySession.ContainsKey(session.FilePath);
        if (!cached)
        {
            StatusText.Text = $"Reading lap list from {session.FileName}…";
            BeginLoading($"Reading laps from {session.FileName}");
        }

        try
        {
            var laps = await GetLapsAsync(session);
            var optimal = TelemetryLoader.CreateOptimalLap(session);
            var fastest = FastestDrivenLap(laps);
            node.Children.Clear();
            if (optimal is not null)
            {
                node.Children.Add(CreateLapNode(optimal, Palette.Optimal));
            }

            if (fastest is not null)
            {
                _fastestLapMs[session.FilePath] = fastest.DurationMs;
                UpdateDriverHeaders(session);
            }

            foreach (var lap in laps.OrderBy(item => item.Index))
            {
                node.Children.Add(CreateLapNode(lap, lap == fastest ? Palette.Fastest : Palette.Text));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or JsonException or ArgumentException)
        {
            node.Children.Clear();
            node.Children.Add(new TreeNode($"Could not read laps: {exception.Message}"));
            StatusText.Text = "Could not read session laps.";
        }
        finally
        {
            if (!cached)
            {
                EndLoading();
            }
        }
    }

    private static TreeNode CreateLapNode(LapInfo lap, IBrush foreground) => new(lap.TreeLabel)
    {
        Lap = lap,
        Key = $"P:{lap.Session.FilePath}|{(lap.IsOptimal ? "optimal" : lap.Index.ToString())}",
        Foreground = foreground
    };

    private LapInfo? SelectedLap => SessionTree.SelectedItem is TreeNode node
        ? node.Lap ?? node.Driver?.OptimalLap
        : null;

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
        else if (node.Date is { } date)
        {
            await SelectDateFastestLapAsync(node, date);
        }
        else if (node.Driver is { } driver)
        {
            if (driver.OptimalLap is { } optimal)
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

    // As in WPF, clicking a date loads the fastest driven lap across that day's sessions.
    private async Task SelectDateFastestLapAsync(TreeNode dateNode, TrackDateGroup date)
    {
        try
        {
            var candidates = new List<LapInfo>();
            foreach (var session in date.Sessions)
            {
                candidates.AddRange(await GetLapsAsync(session));
            }

            if (!ReferenceEquals(SessionTree.SelectedItem, dateNode))
            {
                return;
            }

            if (FastestDrivenLap(candidates) is { } fastest)
            {
                await SelectLapAsync(fastest);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           JsonException or InvalidDataException or ArgumentException)
        {
            await Dialogs.ShowMessageAsync(this, "Laps could not be loaded", exception.Message);
        }
    }

    // Like WPF, picking a session expands it and opens its fastest lap.
    private async Task SelectSessionAsync(TreeNode node, SessionInfo session)
    {
        node.IsExpanded = true;
        await node.EnsureChildrenLoaded();
        var lapNodes = node.Children.Where(child => child.Lap is { IsOptimal: false }).ToArray();
        var best = lapNodes.Where(child => child.Lap is { IsAverage: false } lap &&
                                           double.IsFinite(lap.DurationMs) && lap.DurationMs > 0)
                       .MinBy(child => child.Lap!.DurationMs)
                   ?? lapNodes.MinBy(child => child.Lap!.DurationMs);
        if (best is not null)
        {
            Dispatcher.UIThread.Post(() => SessionTree.SelectedItem = best);
        }
        else
        {
            StatusText.Text = $"{session.FileName}: no laps found.";
        }
    }

    // Clicking a session's fastest-lap time in its header selects that lap (WPF SessionFastestLap_MouseLeftButtonDown).
    private async void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not TextBlock { DataContext: TreeNode { SuffixIsLink: true, Session: not null } node } text ||
            !text.Classes.Contains("suffix") ||
            !e.GetCurrentPoint(text).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        node.IsExpanded = true;
        await node.EnsureChildrenLoaded();
        var fastestLap = FastestDrivenLap(node.Children.Select(child => child.Lap).OfType<LapInfo>());
        var fastestNode = node.Children.FirstOrDefault(child => child.Lap is not null && child.Lap == fastestLap);
        if (fastestNode is null)
        {
            return;
        }

        if (ReferenceEquals(SessionTree.SelectedItem, fastestNode))
        {
            await SelectLapAsync(fastestNode.Lap!);
        }
        else
        {
            SessionTree.SelectedItem = fastestNode;
            Dispatcher.UIThread.Post(() => (SessionTree.TreeContainerFromItem(fastestNode) as Control)?.BringIntoView());
        }
    }

    private async Task SelectLapAsync(LapInfo lap)
    {
        ShowDataPanel();
        _primaryLap = lap;
        var location = _model.LocationOf(lap.Session);
        TrackTitleText.Text = location?.DisplayName ?? lap.Session.FileName;
        var generation = ++_lapGeneration;
        if (_referenceLap is not null && _model.LocationOf(_referenceLap.Session) != location)
        {
            _referenceLap = null;
            _reference = null;
            ClearReferenceButton.IsEnabled = false;
        }

        UpdateLapCards();
        StatusText.Text = $"Loading {lap.Label} telemetry…";
        BeginLoading($"Loading {lap.Session.FileName} · {lap.Label}");
        try
        {
            var referenceLap = _referenceLap;
            var primaryTask = Task.Run(() => TelemetryLoader.ReadTrace(lap));
            var referenceTask = referenceLap is not null && _reference?.Lap != referenceLap
                ? Task.Run<SessionTrace?>(() => TelemetryLoader.ReadTrace(referenceLap))
                : Task.FromResult(_reference);
            var trace = await primaryTask;
            var reference = await referenceTask;
            var segments = await LoadSegmentMapAsync(_model.MeanlinePathOf(lap.Session));
            if (generation != _lapGeneration)
            {
                return;
            }

            if (_referenceLap == referenceLap)
            {
                _reference = reference;
            }

            _primary = trace;
            _segmentMap = segments;
            RefreshChart(resetCursor: true);
            StatusText.Text =
                $"Showing {trace.Points.Count:N0} samples from {lap.Label}. " +
                $"Speed is converted to {(Imperial ? "mph" : "km/h")}; acceleration axes are shown in g.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or JsonException or ArgumentException)
        {
            if (generation != _lapGeneration)
            {
                return;
            }

            _primary = null;
            RefreshChart();
            StatusText.Text = "Could not load lap telemetry.";
            await Dialogs.ShowMessageAsync(this, "Lap could not be loaded", exception.Message);
        }
        finally
        {
            EndLoading();
        }
    }

    private async Task<TrackSegmentMap?> LoadSegmentMapAsync(string? meanlinePath)
    {
        if (meanlinePath is null)
        {
            return null;
        }

        if (_segmentCache.TryGetValue(meanlinePath, out var cached))
        {
            return cached;
        }

        TrackSegmentMap? segments;
        try
        {
            segments = await Task.Run(() => TelemetryLoader.ReadMeanlineSegments(meanlinePath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            segments = null;
        }

        _segmentCache[meanlinePath] = segments;
        return segments;
    }

    private void RefreshChart(bool resetCursor = false)
    {
        if (_loading)
        {
            return;
        }

        var speedFactor = Imperial ? MpsToMph : MpsToKph;
        var traces = new List<SessionTrace>();
        if (_primary is not null)
        {
            traces.Add(_primary);
            if (_reference is not null)
            {
                traces.Add(_reference);
            }
        }

        static string LineLabel(int index) => index == 0 ? "Session" : "Reference";
        static IBrush LineColor(int index) => index == 0 ? Palette.Primary : Palette.Reference;

        IReadOnlyList<ChartLine> Lines(Func<TelemetryPoint, double> select, string suffix = "", string deltaKey = "",
            bool dashed = false) => traces
            .Select((trace, index) => new ChartLine(
                suffix.Length == 0 ? LineLabel(index) : $"{LineLabel(index)} {suffix}",
                trace.Points.Select(point => new ChartValue(point.DistanceM, select(point))).ToArray(),
                LineColor(index), dashed, deltaKey))
            .ToArray();

        var charts = new List<ChartSpec>();
        foreach (var channel in ChannelCatalog.Build(_aimChannels, _model.Settings).Where(item => item.IsEnabled))
        {
            var title = ChannelCatalog.PanelTitle(channel);
            switch (channel.Key)
            {
                case "speed":
                    charts.Add(new ChartSpec(channel.Key, title, Imperial ? "mph" : "km/h", false,
                        Lines(point => point.SpeedMps * speedFactor)));
                    break;
                case "combined-g":
                    var combined = new List<ChartLine>();
                    for (var index = 0; index < traces.Count; index++)
                    {
                        var trace = traces[index];
                        combined.Add(new ChartLine($"{LineLabel(index)} lateral",
                            trace.Points.Select(point => new ChartValue(point.DistanceM, point.LateralG)).ToArray(),
                            LineColor(index), DeltaKey: "lateral"));
                        combined.Add(new ChartLine($"{LineLabel(index)} longitudinal",
                            trace.Points.Select(point => new ChartValue(point.DistanceM, point.LongitudinalG)).ToArray(),
                            LineColor(index), Dashed: true, DeltaKey: "longitudinal"));
                    }

                    charts.Add(new ChartSpec(channel.Key, title, "g", true, combined));
                    break;
                case "longitudinal-g":
                    charts.Add(new ChartSpec(channel.Key, title, "g", true,
                        Lines(point => point.LongitudinalG, "longitudinal", "longitudinal")));
                    break;
                case "lateral-g":
                    charts.Add(new ChartSpec(channel.Key, title, "g", true,
                        Lines(point => point.LateralG, "lateral", "lateral")));
                    break;
                case "delta-time":
                    charts.Add(new ChartSpec(channel.Key, title, "seconds", true,
                        _primary is null || _reference is null
                            ? []
                            : [new ChartLine("A − B", DeltaValues(_primary, _reference), Palette.Primary)]));
                    break;
                default:
                    if (channel.Key.StartsWith(ChannelCatalog.AimPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var name = channel.Key[ChannelCatalog.AimPrefix.Length..];
                        charts.Add(new ChartSpec(channel.Key, title, channel.Units, false, traces
                            .Select((trace, index) => new ChartLine(
                                LineLabel(index), trace.GetChannelValues(name), LineColor(index)))
                            .ToArray()));
                    }

                    break;
            }
        }

        var maxDistance = _primary?.DistanceM ?? 1;
        var segments = _segmentMap is not null && _primary is not null && maxDistance > 0
            ? _segmentMap.ScaledTo(maxDistance)
            : null;
        Trace.SetData(traces, segments, maxDistance, Imperial, charts, resetCursor);
        UpdateLapCards();
    }

    private void UpdateLapCards()
    {
        UpdateLapTypeTag(PrimaryLapTypeText, _primaryLap);
        UpdateLapTypeTag(ReferenceLapTypeText, _referenceLap);
        PrimaryLapDetailsText.Text = _primaryLap is null
            ? string.Empty
            : $"{_model.GetVehicleName(_primaryLap.Session)}  ·  {_model.GetSessionEdits(_primaryLap.Session).TrackCondition}";
        ReferenceLapDetailsText.Text = _referenceLap is null
            ? "Right-click a lap to set reference"
            : $"{_model.GetVehicleName(_referenceLap.Session)}  ·  {_model.GetSessionEdits(_referenceLap.Session).TrackCondition}";
        PrimaryLapText.Text = _primaryLap is null
            ? "—"
            : FormatLapSummary(_primaryLap, _primary?.Lap == _primaryLap
                ? _primary.LapDurationSeconds
                : _primaryLap.DurationMs / 1000d);
        ReferenceLapText.Text = _referenceLap is null
            ? "—"
            : FormatLapSummary(_referenceLap, _reference?.LapDurationSeconds ?? _referenceLap.DurationMs / 1000d);
        if (_primaryLap is null)
        {
            TrackTitleText.Text = "Select a session";
        }

        if (_primary is null || _reference is null)
        {
            LapDeltaText.Text = "—";
            LapDeltaText.Foreground = Palette.White;
        }
        else
        {
            var delta = _primary.LapDurationSeconds - _reference.LapDurationSeconds;
            LapDeltaText.Text = $"{(delta > 0 ? "+" : string.Empty)}{delta:0.000}s";
            LapDeltaText.Foreground = delta <= 0 ? Palette.DeltaGain : Palette.DeltaLoss;
        }

        OnCursorChanged(Trace.CursorDistanceM);
    }

    private string FormatLapSummary(LapInfo lap, double durationSeconds)
    {
        var lapDate = lap.Session.LocalStart.AddMilliseconds(lap.StartTimeSessionMs);
        return $"{_model.GetDriverName(lap.Session)}  ·  {lapDate:MMM d, yyyy}  ·  " +
               $"{lap.Label}  ·  {FormatLapTime(durationSeconds * 1000)}";
    }

    // "Optimal" in purple or "Fastest" in green on the lap cards, as in WPF.
    private void UpdateLapTypeTag(TextBlock tag, LapInfo? lap)
    {
        if (lap is null)
        {
            tag.IsVisible = false;
            return;
        }

        if (lap.IsOptimal)
        {
            tag.Text = "Optimal";
            tag.Foreground = Palette.Optimal;
            tag.IsVisible = true;
            return;
        }

        if (_lapsBySession.TryGetValue(lap.Session.FilePath, out var laps) &&
            laps.Where(candidate => !candidate.IsAverage).MinBy(candidate => candidate.DurationMs) is { } fastest &&
            fastest.Index == lap.Index)
        {
            tag.Text = "Fastest";
            tag.Foreground = Palette.Fastest;
            tag.IsVisible = true;
            return;
        }

        tag.IsVisible = false;
    }

    private void OnCursorChanged(double distanceM)
    {
        if (_primary is null)
        {
            CursorDistanceText.Text = "—";
            return;
        }

        var primaryDistance = Math.Clamp(distanceM, 0, _primary.DistanceM);
        var deltaText = string.Empty;
        if (_reference is not null && primaryDistance <= _reference.DistanceM)
        {
            var delta = (TelemetryLoader.InterpolateTimeMs(_primary.Points, primaryDistance) -
                         TelemetryLoader.InterpolateTimeMs(_reference.Points, primaryDistance)) / 1000d;
            deltaText = $"   ·   Δ {(delta > 0 ? "+" : string.Empty)}{delta:0.000}s";
        }

        var cursorLabel = Imperial ? $"{primaryDistance * 3.28084:0} ft" : $"{primaryDistance:0} m";
        CursorDistanceText.Text = $"{cursorLabel}{deltaText}";
    }

    private static IReadOnlyList<ChartValue> DeltaValues(SessionTrace primary, SessionTrace reference) =>
        primary.Points.Select(point => new ChartValue(
                point.DistanceM,
                point.DistanceM <= reference.DistanceM
                    ? (point.TimeMs - TelemetryLoader.InterpolateTimeMs(reference.Points, point.DistanceM)) / 1000d
                    : double.NaN))
            .ToArray();
}
