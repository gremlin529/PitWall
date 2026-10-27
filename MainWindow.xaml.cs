using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace TrackTelemetryViewer;

public partial class MainWindow : Window
{
    private const double TrackMergeDistanceM = 5 * 1609.344;
    private const string DefaultUnits = "Metric";
    private const string UseSourceLayout = "__use_source_layout__";
    private const string UnspecifiedLayout = "__unspecified_layout__";
    private static readonly Brush PrimaryColor = Brushes.DeepSkyBlue;
    private static readonly Brush ReferenceColor = Brushes.Orange;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly IReadOnlyList<ChartChannelSetting> CoreChartChannels =
    [
        new("speed", "Speed", "km/h", true),
        new("combined-g", "Combined G-forces", "g", true),
        new("longitudinal-g", "Longitudinal G", "g", true),
        new("lateral-g", "Lateral G", "g", true),
        new("delta-time", "Delta Time", "seconds", true)
    ];

    private readonly string _sessionEditsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TrackTelemetryViewer",
        "session-overrides.json");
    private readonly string _localViewerSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TrackTelemetryViewer",
        "viewer-settings.json");
    private readonly string _lastDataFolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TrackTelemetryViewer",
        "last-data-folder.txt");

    private readonly Dictionary<string, SessionEdits> _sessionEdits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _vehicleMappings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TelemetryChart> _chartElements =
        new(StringComparer.OrdinalIgnoreCase);
    private ViewerSettings _viewerSettings = new();
    private IReadOnlyList<string> _customVehicleNames = [];
    private VehicleCatalog _vehicleCatalog = new([], []);
    private IReadOnlyDictionary<string, string> _driverNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly List<SessionInfo> _sessions = [];
    private readonly List<TrackLocation> _locations = [];
    private readonly Dictionary<string, TrackLocation> _trackBySession = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _fastestLapTimesBySession =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<LapInfo>> _lapsBySession = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<IReadOnlyList<LapInfo>>> _lapLoadTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<TrackPoint>> _meanlineCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<IReadOnlyDictionary<string, int>>> _trackStatsTasks =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<SessionInfo> _overviewSessions = [];
    private IReadOnlyDictionary<string, int> _overviewLapCounts =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private SessionInfo? _selectedSession;
    private TrackLocation? _selectedLocation;
    private string? _dataFolder;
    private LapInfo? _primaryLap;
    private LapInfo? _referenceLap;
    private SessionTrace? _primaryTrace;
    private SessionTrace? _referenceTrace;
    private TrackSegmentMap? _segmentMap;
    private readonly Dictionary<string, TrackSegmentMap?> _segmentCache = new(StringComparer.OrdinalIgnoreCase);
    private int _loadGeneration;
    private int _overviewGeneration;
    private int _treeGeneration;
    private int _activeLoads;
    private double _cursorDistanceM;

    public MainWindow()
    {
        InitializeComponent();
        ShowAppVersion();
        LoadSessionEdits();
        RebuildTelemetryCharts();
        RestoreWindowPlacement();
        Closing += (_, _) => SaveWindowPlacement();
    }

    private sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

    private string WindowPlacementPath => Path.Combine(
        Path.GetDirectoryName(_localViewerSettingsPath)!, "window-placement.json");

    private void RestoreWindowPlacement()
    {
        try
        {
            if (!File.Exists(WindowPlacementPath) ||
                JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(WindowPlacementPath)) is not { } placement ||
                !new[] { placement.Left, placement.Top, placement.Width, placement.Height }.All(double.IsFinite) ||
                placement.Width < MinWidth || placement.Height < MinHeight)
            {
                return;
            }

            var bounds = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
            var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var visible = Rect.Intersect(bounds, virtualScreen);
            if (visible.IsEmpty || visible.Width < 100 || visible.Height < 60)
            {
                return;
            }

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;
            if (placement.Maximized)
            {
                WindowState = WindowState.Maximized;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    private void SaveWindowPlacement()
    {
        try
        {
            var bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;
            Directory.CreateDirectory(Path.GetDirectoryName(WindowPlacementPath)!);
            File.WriteAllText(WindowPlacementPath, JsonSerializer.Serialize(new WindowPlacement(
                bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _ = CheckForUpdateAsync();
        var defaultFolder = FindDefaultDataFolder();
        if (defaultFolder is null)
        {
            StatusText.Text = "Choose the folder containing your session JSON files.";
            return;
        }

        await LoadFolderAsync(defaultFolder);
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the folder containing session JSON files",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            await LoadFolderAsync(dialog.FolderName);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left ||
            e.OriginalSource is DependencyObject source &&
            FindAncestor<Button>(source) is not null)
        {
            return;
        }

        DragMove();
    }

    private static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var aliases = _sessions.Select(session => session.VehicleAlias)
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Concat(_viewerSettings.VehicleMappings.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var vehicleNames = _vehicleCatalog.Names
            .Concat(_customVehicleNames)
            .Concat(_vehicleMappings.Values)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var trackAliasItems = _sessions
            .Select(session =>
            {
                var configurationName = GetEffectiveLayoutName(session);
                var sourceTrackName = string.IsNullOrWhiteSpace(session.MeanlineTrackName)
                    ? "Unknown track"
                    : session.MeanlineTrackName.Trim();
                var trackName = _trackBySession.TryGetValue(session.FilePath, out var location)
                    ? location.TrackName
                    : string.IsNullOrWhiteSpace(session.MeanlineTrackName)
                        ? "Unknown track"
                        : session.MeanlineTrackName.Trim();
                var key = CreateTrackAliasKey(sourceTrackName, configurationName);
                var isUnspecifiedWithAlternatives = string.IsNullOrWhiteSpace(configurationName) &&
                    _locations.Any(candidate =>
                        !string.IsNullOrWhiteSpace(candidate.ConfigurationName) &&
                        !candidate.ConfigurationName.Equals("Layout unspecified", StringComparison.OrdinalIgnoreCase) &&
                        candidate.TrackName.Equals(trackName, StringComparison.CurrentCultureIgnoreCase) &&
                        DistanceMeters(candidate.Latitude, candidate.Longitude, session.Latitude, session.Longitude) <=
                        TrackMergeDistanceM);
                var defaultName = string.IsNullOrWhiteSpace(configurationName)
                    ? isUnspecifiedWithAlternatives
                        ? $"{trackName} · Layout unspecified"
                        : trackName
                    : $"{trackName} · {configurationName}";
                var layoutLabel = string.IsNullOrWhiteSpace(configurationName)
                    ? $"{trackName} · Layout unspecified"
                    : defaultName;
                var displayName = _viewerSettings.TrackMappings.TryGetValue(key, out var mappedName)
                    ? mappedName
                    : defaultName;
                return new TrackAliasSetting(key, layoutLabel, defaultName, displayName);
            })
            .GroupBy(alias => alias.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var dialog = new SettingsWindow(
            _viewerSettings,
            aliases,
            trackAliasItems,
            vehicleNames,
            _vehicleCatalog.Names,
            BuildChartChannelSettings())
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var settingsPath = GetViewerSettingsPath();
        try
        {
            var parentDirectory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            File.WriteAllText(settingsPath, JsonSerializer.Serialize(dialog.Settings, JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show(this,
                $"Settings could not be saved to {settingsPath}.{Environment.NewLine}{exception.Message}",
                "Could not save settings", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _viewerSettings = dialog.Settings;
        _vehicleMappings.Clear();
        foreach (var (alias, vehicleName) in _viewerSettings.VehicleMappings)
        {
            _vehicleMappings[alias] = vehicleName;
        }

        _customVehicleNames = _viewerSettings.CustomVehicleNames;
        UpdateVehicleChoices();
        var selectedSession = _selectedSession;
        var selectedLocationSessions = _selectedLocation?.Sessions;
        BuildTrackLocations();
        RebuildTelemetryCharts();

        BuildSessionTree();
        if (selectedSession is not null)
        {
            var sessionNode = FindSessionNode(selectedSession);
            if (sessionNode is not null)
            {
                ExpandAncestors(sessionNode);
                sessionNode.IsSelected = true;
            }
        }
        else if (selectedLocationSessions is { Count: > 0 } &&
                 _trackBySession.TryGetValue(selectedLocationSessions[0].FilePath, out var refreshedLocation))
        {
            _selectedLocation = refreshedLocation;
            var trackNode = SessionTree.Items.OfType<TreeViewItem>()
                .FirstOrDefault(node => node.Tag is TrackLocation location &&
                                        location.Key == refreshedLocation.Key);
            if (trackNode is not null)
            {
                trackNode.IsSelected = true;
            }
        }

        if (selectedSession is null && _selectedLocation is not null)
        {
            await ShowTrackOverviewAsync(_selectedLocation, null);
        }

        StatusText.Text = $"Settings saved to {settingsPath}.";
    }

    private IReadOnlyList<ChartChannelSetting> BuildChartChannelSettings()
    {
        var channels = CoreChartChannels
            .Select(channel => new ChartChannelSetting(
                channel.Key,
                channel.Name,
                channel.Units,
                _viewerSettings.ChannelVisibility.TryGetValue(channel.Key, out var enabled)
                    ? enabled
                    : channel.IsEnabled))
            .ToList();
        var aimChannels = _sessions
            .Where(session => session.AiMData is not null)
            .SelectMany(session => session.AiMData!.Laps)
            .SelectMany(lap => lap.Channels)
            .GroupBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(channel => GetAiMChannelOrder(channel.Name))
            .ThenBy(channel => channel.Name, StringComparer.CurrentCultureIgnoreCase);
        channels.AddRange(aimChannels.Select(channel =>
        {
            var key = GetAiMChartKey(channel.Name);
            return new ChartChannelSetting(
                key,
                string.IsNullOrWhiteSpace(channel.Units)
                    ? GetAiMChannelLabel(channel.Name)
                    : $"{GetAiMChannelLabel(channel.Name)} ({channel.Units})",
                channel.Units,
                _viewerSettings.ChannelVisibility.TryGetValue(key, out var enabled) && enabled);
        }));
        var order = _viewerSettings.ChannelOrder
            .Select((key, index) => (key, index))
            .GroupBy(item => item.key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(item => item.key, item => item.index, StringComparer.OrdinalIgnoreCase);
        return channels
            .Select((channel, index) => (channel, index))
            .OrderBy(item => order.TryGetValue(item.channel.Key, out var rank) ? rank : int.MaxValue)
            .ThenBy(item => item.index)
            .Select(item => item.channel)
            .ToArray();
    }

    private void BeginLoading(string detail)
    {
        _activeLoads++;
        LoadingDetailText.Text = detail;
        LoadingIndicator.Visibility = Visibility.Visible;
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
            LoadingIndicator.Visibility = Visibility.Collapsed;
            LoadingDetailText.Text = string.Empty;
        }
    }

    private static int GetAiMChannelOrder(string channelName)
    {
        if (channelName.Contains("rpm", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (channelName.Contains("throttle", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (channelName.Contains("brake", StringComparison.OrdinalIgnoreCase) ||
            channelName.Contains("pressure", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (channelName.Contains("steer", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        return 4;
    }

    private static string GetAiMChannelLabel(string channelName)
    {
        if (channelName.Contains("rpm", StringComparison.OrdinalIgnoreCase))
        {
            return "RPM";
        }

        if (channelName.Contains("throttle", StringComparison.OrdinalIgnoreCase))
        {
            return "Throttle Position";
        }

        if (channelName.Contains("brake", StringComparison.OrdinalIgnoreCase) &&
            channelName.Contains("press", StringComparison.OrdinalIgnoreCase))
        {
            return "Brake Pressure";
        }

        if (channelName.Contains("brake", StringComparison.OrdinalIgnoreCase))
        {
            return "Brake Position";
        }

        if (channelName.Contains("steer", StringComparison.OrdinalIgnoreCase))
        {
            return "Steering";
        }

        return channelName;
    }

    private static string GetAiMChartKey(string channelName) => $"aim:{channelName}";

    private string GetViewerSettingsPath() =>
        _dataFolder is null
            ? _localViewerSettingsPath
            : Path.Combine(_dataFolder, "viewer-settings.json");

    private bool SaveViewerSettings(ViewerSettings settings, string failureTitle, string failureMessage)
    {
        var settingsPath = GetViewerSettingsPath();
        try
        {
            var parentDirectory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, JsonOptions));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show(this,
                $"{failureMessage} {settingsPath}.{Environment.NewLine}{exception.Message}",
                failureTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void UpdateVehicleChoices()
    {
        var names = _vehicleCatalog.Names
            .Concat(_customVehicleNames)
            .Concat(_vehicleMappings.Values)
            .Where(name => !IsVehicleHidden(name))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        _vehicleChoices = names;
    }

    private IReadOnlyList<string> _vehicleChoices = [];

    private bool UseImperialDistance =>
        _viewerSettings.DistanceUnits.Equals("Imperial", StringComparison.OrdinalIgnoreCase);

    private TelemetryChart SpeedChart => _chartElements["speed"];

    private string FormatTrackLength(double distanceM) => UseImperialDistance
        ? $"{distanceM / 1609.344:0.00} mi"
        : $"{distanceM / 1000:0.00} km";

    private void ApplyDistanceUnits()
    {
        var useImperial = UseImperialDistance;
        SpeedChart.Unit = useImperial ? "mph" : "km/h";
        foreach (var chart in _chartElements.Values)
        {
            chart.UseImperialDistance = useImperial;
        }
        if (_selectedLocation is not null && TrackOverviewPanel.Visibility == Visibility.Visible)
        {
            UpdateTrackOverviewDistanceLabels(_selectedLocation);
        }

        if (_primaryTrace is not null)
        {
            UpdateCharts();
            UpdateStatistics();
            if (DataViewPanel.Visibility == Visibility.Visible)
            {
                StatusText.Text =
                    $"Showing {_primaryTrace.Points.Count:N0} samples from {_primaryTrace.Lap.Label}. " +
                    $"Speed is converted to {SpeedChart.Unit}; acceleration axes are shown in g.";
            }
        }
    }

    private void RebuildTelemetryCharts()
    {
        ChartListPanel.Children.Clear();
        _chartElements.Clear();
        foreach (var channel in BuildChartChannelSettings())
        {
            var chart = GetOrCreateChart(channel);
            if (!channel.IsEnabled)
            {
                continue;
            }

            ChartListPanel.Children.Add(new Border
            {
                Background = (Brush)FindResource("PanelBrush"),
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Height = 190,
                Margin = new Thickness(0, 0, 0, 9),
                Child = chart
            });
        }

        ApplyDistanceUnits();
        if (_primaryTrace is not null)
        {
            UpdateCharts();
        }
    }

    private TelemetryChart GetOrCreateChart(ChartChannelSetting channel)
    {
        if (_chartElements.TryGetValue(channel.Key, out var chart))
        {
            return chart;
        }

        var title = channel.Key switch
        {
            "speed" => "SPEED",
            "combined-g" => "G-FORCE",
            "longitudinal-g" => "LONGITUDINAL G",
            "lateral-g" => "LATERAL G",
            "delta-time" => "TIME DELTA  ·  A − B",
            _ => channel.Key.StartsWith("aim:", StringComparison.OrdinalIgnoreCase)
                ? GetAiMChannelLabel(channel.Key["aim:".Length..]).ToUpperInvariant()
                : channel.Name
        };
        chart = new TelemetryChart
        {
            Title = title,
            Unit = channel.Key.Equals("speed", StringComparison.OrdinalIgnoreCase)
                ? UseImperialDistance ? "mph" : "km/h"
                : channel.Units,
            SymmetricRange = channel.Key is "combined-g" or "longitudinal-g" or "lateral-g" or "delta-time"
        };
        chart.DistanceHovered += Chart_DistanceHovered;
        _chartElements.Add(channel.Key, chart);
        return chart;
    }

    private void UpdateTrackOverviewDistanceLabels(TrackLocation location)
    {
        OverviewTrackSubtitle.Text =
            $"{location.DisplayName} · {FormatTrackLength(location.LengthM)} · " +
            $"{location.Sessions.Count(session => !IsVehicleHidden(GetVehicleName(session)))} " +
            $"sessions at {location.Latitude:F5}, {location.Longitude:F5}";
        OverviewTrackLength.Text = location.LengthM > 0
            ? FormatTrackLength(location.LengthM)
            : "—";
    }

    private void LoadViewerSettings(string folder)
    {
        _vehicleMappings.Clear();
        _viewerSettings = new ViewerSettings();
        var settingsPaths = new[]
        {
            _localViewerSettingsPath,
            Path.Combine(folder, "viewer-settings.json")
        }.Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var settingsPath in settingsPaths)
        {
            if (!File.Exists(settingsPath))
            {
                continue;
            }

            try
            {
                var loadedSettings = JsonSerializer.Deserialize<ViewerSettings>(File.ReadAllText(settingsPath))
                                     ?? throw new InvalidDataException("The settings file did not contain a JSON object.");
                _viewerSettings.DistanceUnits =
                    loadedSettings.DistanceUnits?.Equals("Imperial", StringComparison.OrdinalIgnoreCase) == true
                        ? "Imperial"
                        : DefaultUnits;
                _viewerSettings.CustomVehicleNames = _viewerSettings.CustomVehicleNames
                    .Concat(loadedSettings.CustomVehicleNames ?? [])
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                _viewerSettings.HiddenVehicleNames = _viewerSettings.HiddenVehicleNames
                    .Concat(loadedSettings.HiddenVehicleNames ?? [])
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                foreach (var (trackAlias, trackName) in loadedSettings.TrackMappings ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(trackAlias) && !string.IsNullOrWhiteSpace(trackName))
                    {
                        _viewerSettings.TrackMappings[trackAlias] = trackName;
                    }
                }

                foreach (var (sessionGuid, layoutName) in loadedSettings.SessionLayoutAssignments ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(sessionGuid) && !string.IsNullOrWhiteSpace(layoutName))
                    {
                        _viewerSettings.SessionLayoutAssignments[sessionGuid] = layoutName;
                    }
                }

                foreach (var (sessionGuid, vehicleName) in loadedSettings.SessionVehicleMappings ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(sessionGuid) && !string.IsNullOrWhiteSpace(vehicleName))
                    {
                        _viewerSettings.SessionVehicleMappings[sessionGuid] = vehicleName;
                    }
                }

                foreach (var (channelKey, isVisible) in loadedSettings.ChannelVisibility ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(channelKey))
                    {
                        _viewerSettings.ChannelVisibility[channelKey] = isVisible;
                    }
                }
                if (loadedSettings.ChannelOrder is { Count: > 0 })
                {
                    _viewerSettings.ChannelOrder = loadedSettings.ChannelOrder
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                foreach (var (alias, vehicleName) in loadedSettings.VehicleMappings ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(alias) && !string.IsNullOrWhiteSpace(vehicleName))
                    {
                        _vehicleMappings[alias] = vehicleName;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               JsonException or InvalidDataException)
            {
                MessageBox.Show(this,
                    $"Viewer settings could not be loaded from {settingsPath}.{Environment.NewLine}{exception.Message}",
                    "Could not load settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        foreach (var (alias, vehicleName) in _viewerSettings.VehicleMappings)
        {
            if (!string.IsNullOrWhiteSpace(alias) && !string.IsNullOrWhiteSpace(vehicleName))
            {
                _vehicleMappings[alias] = vehicleName;
            }
        }

        var legacyMappingPath = Path.Combine(folder, "vehicle-name-mappings.json");
        if (File.Exists(legacyMappingPath))
        {
            try
            {
                var legacyMappings = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(legacyMappingPath));
                if (legacyMappings is not null)
                {
                    foreach (var (alias, vehicleName) in legacyMappings)
                    {
                        _vehicleMappings.TryAdd(alias, vehicleName);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                MessageBox.Show(this,
                    $"Legacy vehicle mappings could not be loaded from {legacyMappingPath}.{Environment.NewLine}{exception.Message}",
                    "Could not load vehicle mappings", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        _viewerSettings.VehicleMappings = new Dictionary<string, string>(
            _vehicleMappings, StringComparer.OrdinalIgnoreCase);
        _customVehicleNames = _viewerSettings.CustomVehicleNames;
    }

    private void SaveLastDataFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_lastDataFolderPath)!);
            File.WriteAllText(_lastDataFolderPath, folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this,
                $"The last-used data folder could not be remembered.{Environment.NewLine}{exception.Message}",
                "Could not save last data folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task LoadFolderAsync(string folder)
    {
        if (!Directory.Exists(folder))
        {
            MessageBox.Show(this, $"The selected folder does not exist:\n{folder}",
                "Folder not found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BeginLoading("Scanning session files…");
        try
        {
        var treeGeneration = ++_treeGeneration;
        StatusText.Text = "Scanning session files…";
        UpdateLoadingDetail("Finding telemetry files…");
        var packageImport = await Task.Run(() => ExtractPackages(folder, _pendingPackages.ToArray()));
        _pendingPackages.Clear();
        var performanceFiles = TelemetryLoader.EnumerateFilesRecursively(folder, "autosport-performance-*.json");
        var aimFiles = TelemetryLoader.EnumerateFilesRecursively(folder, "*.xrk");
        var csvFiles = TelemetryLoader.EnumerateAimCsvFiles(folder);
        var files = performanceFiles.Concat(aimFiles).Concat(csvFiles)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            MessageBox.Show(this,
                "No Garmin performance JSON or AiM XRK session files were found in the selected folder.",
                "No session files", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = "No session files found.";
            return;
        }

        var dataFileIndex = TelemetryLoader.BuildDataFileIndex(folder);
        List<SessionInfo> loaded = [];
        List<string> failures = [];
        await Task.Run(() =>
        {
            foreach (var file in files)
            {
                Dispatcher.Invoke(() => UpdateLoadingDetail($"Reading {Path.GetFileName(file)}"));
                try
                {
                    var extension = Path.GetExtension(file);
                    loaded.Add(extension.Equals(".xrk", StringComparison.OrdinalIgnoreCase)
                        ? TelemetryLoader.ReadAiMSessionInfo(file)
                        : extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
                            ? TelemetryLoader.ReadAimCsvSessionInfo(file, GetCsvShiftSeconds)
                            : TelemetryLoader.ReadSessionInfo(file, dataFileIndex));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                   InvalidDataException or ArgumentOutOfRangeException or
                                                   JsonException or Win32Exception or InvalidOperationException)
                {
                    failures.Add($"{Path.GetFileName(file)}: {exception.Message}");
                }
            }
        });

        _sessions.Clear();
        _csvOriginals.Clear();
        _sessions.AddRange(loaded.OrderBy(session => session.LocalStart));
        _dataFolder = folder;
        LoadViewerSettings(folder);
        ApplyImportedCorrections(packageImport);
        ReportImport(packageImport);
        try
        {
            _vehicleCatalog = TelemetryLoader.ReadVehicleCatalog(folder);
            UpdateVehicleChoices();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           JsonException or InvalidDataException)
        {
            _vehicleChoices = [];
            _vehicleCatalog = new VehicleCatalog([], []);
            MessageBox.Show(this, exception.Message, "Vehicle catalog could not be loaded",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        try
        {
            _driverNames = TelemetryLoader.ReadDriverNames(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _driverNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            MessageBox.Show(this, exception.Message, "Driver profiles could not be loaded",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _lapsBySession.Clear();
        _fastestLapTimesBySession.Clear();
        _lapLoadTasks.Clear();
        foreach (var session in _sessions.Where(session => session.AiMData is not null))
        {
            var laps = TelemetryLoader.ReadLaps(session);
            _lapsBySession[session.FilePath] = laps;
            if (laps.MinBy(lap => lap.DurationMs) is { } fastestLap)
            {
                _fastestLapTimesBySession[session.FilePath] = fastestLap.DurationMs;
            }
        }

        RebuildTelemetryCharts();
        _meanlineCache.Clear();
        _segmentCache.Clear();
        _segmentMap = null;
        _trackStatsTasks.Clear();        _referenceLap = null;
        _referenceTrace = null;
        BuildTrackLocations();
        DataFolderText.Text = folder;
        SessionCountText.Text = $"{_sessions.Count} SESSIONS";
        BuildSessionTree();

        if (failures.Count > 0)
        {
            var details = string.Join(Environment.NewLine, failures.Take(8));
            if (failures.Count > 8)
            {
                details += $"{Environment.NewLine}…and {failures.Count - 8} more.";
            }

            MessageBox.Show(this,
                $"Loaded {_sessions.Count} of {files.Length} files. Some files could not be read:{Environment.NewLine}{details}",
                "Session scan completed with errors", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (_sessions.Count == 0)
        {
            StatusText.Text = "No valid sessions found.";
            return;
        }

        SaveLastDataFolder(folder);
        StatusText.Text = $"Loaded {_sessions.Count} sessions across {_locations.Count} tracks.";
        SelectFirstSession();
        _ = LoadFastestLapHeadersAsync(treeGeneration);
        }
        finally
        {
            EndLoading();
        }
    }

    private void BuildTrackLocations()
    {
        _locations.Clear();
        _trackBySession.Clear();
        ApplyCsvTrackAssignments();

        var sessions = _sessions.OrderBy(session => session.Latitude)
            .ThenBy(session => session.Longitude)
            .ThenBy(session => session.LocalStart)
            .ToArray();
        var parents = Enumerable.Range(0, sessions.Length).ToArray();

        int Find(int index)
        {
            if (parents[index] != index)
            {
                parents[index] = Find(parents[index]);
            }

            return parents[index];
        }

        void Union(int left, int right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot != rightRoot)
            {
                parents[rightRoot] = leftRoot;
            }
        }

        for (var left = 0; left < sessions.Length; left++)
        {
            for (var right = left + 1; right < sessions.Length; right++)
            {
                if (DistanceMeters(sessions[left].Latitude, sessions[left].Longitude,
                        sessions[right].Latitude, sessions[right].Longitude) <= TrackMergeDistanceM)
                {
                    Union(left, right);
                }
            }
        }

        foreach (var cluster in Enumerable.Range(0, sessions.Length).GroupBy(Find))
        {
            var nearbySessions = cluster.Select(index => sessions[index])
                .OrderBy(session => session.LocalStart)
                .ToArray();
            var clusterLatitude = nearbySessions.Average(session => session.Latitude);
            var clusterLongitude = nearbySessions.Average(session => session.Longitude);
            var trackName = nearbySessions.Where(session => !string.IsNullOrWhiteSpace(session.MeanlineTrackName))
                .GroupBy(session => session.MeanlineTrackName, StringComparer.CurrentCultureIgnoreCase)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefault() ?? $"Track {clusterLatitude:F2}, {clusterLongitude:F2}";

            var distinctLayouts = nearbySessions
                .Select(GetEffectiveLayoutName)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var displayGroups = nearbySessions.GroupBy(session =>
            {
                var effectiveConfiguration = GetEffectiveLayoutName(session);
                var effectiveTrackName = trackName;
                var configurationLabel = string.IsNullOrWhiteSpace(effectiveConfiguration)
                    ? distinctLayouts.Length > 1 ? "Layout unspecified" : string.Empty
                    : effectiveConfiguration;
                var aliasKey = CreateTrackAliasKey(
                    string.IsNullOrWhiteSpace(session.MeanlineTrackName)
                        ? "Unknown track"
                        : effectiveTrackName,
                    effectiveConfiguration);
                return _viewerSettings.TrackMappings.TryGetValue(aliasKey, out var mappedName) &&
                       !string.IsNullOrWhiteSpace(mappedName)
                    ? mappedName.Trim()
                    : string.IsNullOrWhiteSpace(configurationLabel)
                        ? effectiveTrackName
                        : $"{effectiveTrackName} · {configurationLabel}";
            }, StringComparer.CurrentCultureIgnoreCase);

            foreach (var displayGroup in displayGroups)
            {
                var members = displayGroup.ToArray();
                var latitude = members.Average(session => session.Latitude);
                var longitude = members.Average(session => session.Longitude);
                var memberConfigurations = members.Select(GetEffectiveLayoutName)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                var configurationName = memberConfigurations.Length == 1
                    ? string.IsNullOrWhiteSpace(memberConfigurations[0]) && distinctLayouts.Length > 1
                        ? "Layout unspecified"
                        : memberConfigurations[0]
                    : "Combined layouts";
                var locationTrackName = members.Where(session => !string.IsNullOrWhiteSpace(session.MeanlineTrackName))
                    .GroupBy(session => session.MeanlineTrackName.Trim(), StringComparer.CurrentCultureIgnoreCase)
                    .OrderByDescending(group => group.Count())
                    .Select(group => group.Key)
                    .FirstOrDefault() ?? trackName;
                var key = string.Create(CultureInfo.InvariantCulture,
                    $"{latitude:F4},{longitude:F4}|{displayGroup.Key.ToUpperInvariant()}");
                var lengthM = members.Where(session => session.MeanlineLengthM > 0)
                    .Select(session => session.MeanlineLengthM)
                    .DefaultIfEmpty(0)
                    .Average();
                var meanlinePath = members.FirstOrDefault(session => session.MeanlinePath is not null)?.MeanlinePath;
                var location = new TrackLocation(
                    key, latitude, longitude, members, locationTrackName, configurationName, lengthM, meanlinePath,
                    displayGroup.Key);
                _locations.Add(location);

                foreach (var session in members)
                {
                    _trackBySession[session.FilePath] = location;
                }
            }
        }
    }

    private static string CreateTrackAliasKey(string trackName, string configurationName) =>
        JsonSerializer.Serialize(new[] { trackName.Trim(), configurationName.Trim() });

    private string GetEffectiveLayoutName(SessionInfo session)
    {
        if (_viewerSettings.SessionLayoutAssignments.TryGetValue(session.SessionGuid, out var assignedLayout))
        {
            return assignedLayout.Equals(UnspecifiedLayout, StringComparison.OrdinalIgnoreCase) ||
                   assignedLayout.Equals("Layout unspecified", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : assignedLayout.Trim();
        }

        return session.MeanlineConfigurationName.Equals(
            "Layout unspecified", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : session.MeanlineConfigurationName.Trim();
    }

    private ContextMenu? CreateLayoutContextMenu(TrackLocation location)
    {
        if (!location.ConfigurationName.Equals("Layout unspecified", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var availableLayouts = _locations
            .Where(candidate =>
                !ReferenceEquals(candidate, location) &&
                candidate.TrackName.Equals(location.TrackName, StringComparison.CurrentCultureIgnoreCase) &&
                !string.IsNullOrWhiteSpace(candidate.ConfigurationName) &&
                !candidate.ConfigurationName.Equals("Layout unspecified", StringComparison.OrdinalIgnoreCase) &&
                DistanceMeters(candidate.Latitude, candidate.Longitude, location.Latitude, location.Longitude) <=
                TrackMergeDistanceM)
            .Select(candidate => candidate.ConfigurationName)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (availableLayouts.Length == 0)
        {
            return null;
        }

        var menu = CreateThemedContextMenu();
        var itemStyle = (Style)FindResource("DarkMenuItemStyle");
        var layoutMenu = new MenuItem
        {
            Header = "Choose layout",
            Style = itemStyle,
            ItemContainerStyle = itemStyle
        };
        foreach (var layoutName in availableLayouts)
        {
            var item = new MenuItem
            {
                Header = layoutName,
                Style = itemStyle,
                Tag = new TrackLayoutAssignment(location, layoutName)
            };
            item.Click += AssignTrackLayout_Click;
            layoutMenu.Items.Add(item);
        }

        menu.Items.Add(layoutMenu);
        return menu;
    }

    private void AssignTrackLayout_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: TrackLayoutAssignment assignment })
        {
            return;
        }

        var assignments = new Dictionary<string, string>(
            _viewerSettings.SessionLayoutAssignments, StringComparer.OrdinalIgnoreCase);
        foreach (var session in assignment.Location.Sessions)
        {
            assignments[session.SessionGuid] = assignment.LayoutName;
        }

        var updatedSettings = new ViewerSettings
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
            TrackMappings = new Dictionary<string, string>(
                _viewerSettings.TrackMappings, StringComparer.OrdinalIgnoreCase),
            SessionLayoutAssignments = assignments
        };
        var settingsPath = GetViewerSettingsPath();
        try
        {
            var parentDirectory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            File.WriteAllText(settingsPath, JsonSerializer.Serialize(updatedSettings, JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show(this,
                $"The layout choice could not be saved to {settingsPath}.{Environment.NewLine}{exception.Message}",
                "Could not save layout", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _viewerSettings = updatedSettings;
        BuildTrackLocations();
        BuildSessionTree();

        if (assignment.Location.Sessions.Count > 0 &&
            _trackBySession.TryGetValue(assignment.Location.Sessions[0].FilePath, out var assignedLocation))
        {
            var trackNode = SessionTree.Items.OfType<TreeViewItem>()
                .FirstOrDefault(node => node.Tag is TrackLocation location &&
                                        location.Key == assignedLocation.Key);
            if (trackNode is not null)
            {
                trackNode.IsSelected = true;
            }
        }

        StatusText.Text = $"Layout set to {assignment.LayoutName} and saved to {settingsPath}.";
    }

    private void BuildSessionTree()
    {
        SessionTree.Items.Clear();
        foreach (var location in _locations.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var visibleSessions = location.Sessions.Where(session => !IsVehicleHidden(GetVehicleName(session)))
                .ToArray();
            if (visibleSessions.Length == 0)
            {
                continue;
            }

            var header = new TextBlock { Text = $"{location.DisplayName}  ({visibleSessions.Length})" };
            var locationNode = new TreeViewItem
            {
                Header = header,
                Tag = location,
                IsExpanded = false,
                FontWeight = FontWeights.SemiBold
            };
            var layoutContextMenu = AddCsvTrackMenu(location, CreateLayoutContextMenu(location));
            if (layoutContextMenu is not null)
            {
                locationNode.ContextMenu = layoutContextMenu;
            }

            foreach (var carGroup in visibleSessions.GroupBy(GetVehicleName, StringComparer.CurrentCultureIgnoreCase)
                         .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                var carSessions = carGroup.OrderBy(session => session.LocalStart).ToArray();
                var carNode = new TreeViewItem
                {
                    Header = $"{carGroup.Key}  ({carSessions.Length})",
                    Tag = new TrackCarGroup(carGroup.Key, carSessions),
                    IsExpanded = false,
                    FontWeight = FontWeights.Normal,
                    Foreground = new SolidColorBrush(Color.FromRgb(151, 164, 177))
                };

                carNode.ContextMenu = CreateVehicleContextMenu((TrackCarGroup)carNode.Tag);

                foreach (var driverGroup in carSessions
                             .GroupBy(GetDriverName, StringComparer.CurrentCultureIgnoreCase)
                             .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase))
                {
                    var driverSessions = driverGroup.OrderBy(session => session.LocalStart).ToArray();
                    var optimalLap = driverSessions
                        .Select(TelemetryLoader.CreateOptimalLap)
                        .OfType<LapInfo>()
                        .Where(lap => double.IsFinite(lap.DurationMs) && lap.DurationMs > 0)
                        .MinBy(lap => lap.DurationMs);
                    var driverHeader = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    driverHeader.Children.Add(new TextBlock
                    {
                        Text = $"{driverGroup.Key}  ({driverSessions.Length})",
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    if (optimalLap is not null)
                    {
                        driverHeader.Children.Add(new TextBlock
                        {
                            Text = $"  ·  Optimal {FormatLapTime(optimalLap.DurationMs / 1000d)}",
                            Foreground = Brushes.MediumPurple,
                            Margin = new Thickness(6, 0, 0, 0),
                            VerticalAlignment = VerticalAlignment.Center
                        });
                    }

                    var driverNode = new TreeViewItem
                    {
                        Header = driverHeader,
                        Tag = new TrackDriverGroup(driverGroup.Key, driverSessions, optimalLap),
                        IsExpanded = false,
                        FontWeight = FontWeights.Normal,
                        Foreground = new SolidColorBrush(Color.FromRgb(151, 164, 177))
                    };
                    if (optimalLap is not null)
                    {
                        driverNode.ContextMenu = CreateLapContextMenu(optimalLap);
                    }

                    foreach (var dateGroup in driverSessions.GroupBy(session => session.LocalStart.Date)
                                 .OrderBy(group => group.Key))
                    {
                        var dateSessions = dateGroup.ToArray();
                        var dateInfo = new TrackDateGroup(dateGroup.Key, dateSessions);
                        var dateNode = new TreeViewItem
                        {
                            Header = CreateDateHeader(dateInfo, driverSessions),
                            Tag = dateInfo,
                            IsExpanded = false,
                            FontWeight = FontWeights.Normal,
                            Foreground = new SolidColorBrush(Color.FromRgb(151, 164, 177))
                        };
                        var dateMenu = CreateThemedContextMenu();
                        var dateFastestItem = new MenuItem
                        {
                            Header = "Use fastest lap as reference",
                            Style = (Style)FindResource("DarkMenuItemStyle"),
                            Tag = dateInfo
                        };
                        dateFastestItem.Click += UseSessionFastestAsReference_Click;
                        dateMenu.Items.Add(dateFastestItem);
                        dateMenu.Items.Add(CreateAssignVehicleMenuItem(
                            "Assign vehicle to this date's sessions",
                            [],
                            dateSessions.Select(session => session.SessionGuid)
                                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                            dateSessions));
                        dateMenu.Items.Add(CreateExportMenuItem(dateSessions.ToArray(), dateInfo.Date.ToString("yyyy-MM-dd")));
                        dateNode.ContextMenu = dateMenu;

                        foreach (var session in dateSessions)
                        {
                            var sessionNode = new TreeViewItem
                            {
                                Header = FormatSessionHeader(
                                    session,
                                    _fastestLapTimesBySession.TryGetValue(session.FilePath, out var fastestDuration)
                                        ? fastestDuration
                                        : null,
                                    IsFastestSessionOfDate(session, dateSessions)),
                                Tag = session,
                                FontWeight = FontWeights.Normal
                            };
                            sessionNode.ContextMenu = CreateSessionReferenceContextMenu(session);
                            if (IsWetSession(session))
                            {
                                sessionNode.Foreground = Brushes.DeepSkyBlue;
                            }

                            sessionNode.Expanded += SessionNode_Expanded;
                            dateNode.Items.Add(sessionNode);
                        }

                        driverNode.Items.Add(dateNode);
                    }

                    carNode.Items.Add(driverNode);
                }

                locationNode.Items.Add(carNode);
            }

            SessionTree.Items.Add(locationNode);
        }
    }

    private void SelectFirstSession()
    {
        if (SessionTree.Items.Count > 0 && SessionTree.Items[0] is TreeViewItem firstTrack)
        {
            firstTrack.IsSelected = true;
        }
    }

    private async void SessionTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem selectedNode)
        {
            return;
        }

        switch (selectedNode.Tag)
        {
            case TrackLocation location:
                _selectedLocation = location;
                _selectedSession = null;
                _primaryLap = null;
                await ShowTrackOverviewAsync(location, null);
                break;
            case TrackCarGroup car:
                _selectedSession = null;
                _primaryLap = null;
                await ShowTrackOverviewAsync(_trackBySession[car.Sessions[0].FilePath], car.Sessions, car.Name);
                break;
            case TrackDriverGroup driver:
                if (driver.OptimalLap is not null)
                {
                    await SelectLapAsync(driver.OptimalLap);
                }
                else
                {
                    _selectedSession = null;
                    _primaryLap = null;
                    await ShowTrackOverviewAsync(
                        _trackBySession[driver.Sessions[0].FilePath], driver.Sessions, driver.Name);
                }

                break;
            case TrackDateGroup dateGroup:
                await SelectDateFastestLapAsync(dateGroup, selectedNode);
                break;
            case SessionInfo session:
                await SelectSessionAsync(session, selectedNode);
                break;
            case LapInfo lap:
                await SelectLapAsync(lap);
                break;
        }
    }

    private async Task SelectDateFastestLapAsync(TrackDateGroup dateGroup, TreeViewItem dateNode)
    {
        try
        {
            var candidates = new List<LapInfo>();
            foreach (var session in dateGroup.Sessions)
            {
                if (!_lapsBySession.TryGetValue(session.FilePath, out var laps))
                {
                    var sessionToRead = session;
                    laps = await Task.Run(() => TelemetryLoader.ReadLaps(sessionToRead));
                    _lapsBySession[session.FilePath] = laps;
                }

                candidates.AddRange(laps);
            }

            if (!dateNode.IsSelected)
            {
                return;
            }

            var fastestLap = candidates
                .Where(lap => !lap.IsAverage && double.IsFinite(lap.DurationMs) && lap.DurationMs > 0)
                .MinBy(lap => lap.DurationMs);
            if (fastestLap is not null)
            {
                await SelectLapAsync(fastestLap);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           JsonException or InvalidDataException or ArgumentException)
        {
            MessageBox.Show(this, exception.Message, "Laps could not be loaded",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SelectSessionAsync(SessionInfo session, TreeViewItem sessionNode)
    {
        _selectedSession = session;
        _selectedLocation = _trackBySession[session.FilePath];
        DataViewPanel.Visibility = Visibility.Visible;
        TrackOverviewPanel.Visibility = Visibility.Collapsed;
        TrackTitleText.Text = _selectedLocation.DisplayName;

        if (_referenceLap is not null &&
            (!_trackBySession.TryGetValue(_referenceLap.Session.FilePath, out var referenceTrack) ||
             !ReferenceEquals(referenceTrack, _selectedLocation)))
        {
            _referenceLap = null;
            _referenceTrace = null;
            UpdateReferenceHeader();
        }

        var laps = await PopulateLapNodesAsync(session, sessionNode);
        if (laps is null)
        {
            return;
        }

        sessionNode.IsExpanded = true;
        var bestLap = laps.MinBy(item => item.DurationMs);
        var bestNode = sessionNode.Items.OfType<TreeViewItem>()
            .FirstOrDefault(node => node.Tag is LapInfo item && item == bestLap);
        if (bestNode is not null)
        {
            bestNode.IsSelected = true;
        }
    }

    private async void SessionNode_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem { Tag: SessionInfo session } sessionNode)
        {
            await PopulateLapNodesAsync(session, sessionNode);
        }
    }

    private async Task<IReadOnlyList<LapInfo>?> PopulateLapNodesAsync(
        SessionInfo session,
        TreeViewItem sessionNode)
    {
        if (!_lapsBySession.TryGetValue(session.FilePath, out var laps))
        {
            StatusText.Text = $"Reading lap list from {session.FileName}…";
            BeginLoading($"Reading laps from {session.FileName}");
            try
            {
                if (!_lapLoadTasks.TryGetValue(session.FilePath, out var loadTask))
                {
                    loadTask = Task.Run(() => TelemetryLoader.ReadLaps(session));
                    _lapLoadTasks[session.FilePath] = loadTask;
                }

                laps = await loadTask;
                _lapsBySession[session.FilePath] = laps;
                _lapLoadTasks.Remove(session.FilePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               JsonException or InvalidDataException or ArgumentException)
            {
                _lapLoadTasks.Remove(session.FilePath);
                StatusText.Text = "Could not read session laps.";
                MessageBox.Show(this, exception.Message, "Laps could not be loaded",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
            finally
            {
                EndLoading();
            }
        }

        if (!sessionNode.Items.OfType<TreeViewItem>().Any(node => node.Tag is LapInfo { IsOptimal: false }))
        {
            var optimalLap = TelemetryLoader.CreateOptimalLap(session);
            if (optimalLap is not null)
            {
                sessionNode.Items.Add(CreateLapTreeNode(optimalLap, Brushes.MediumPurple));
            }

            var fastestLap = laps
                .Where(lap => !lap.IsAverage && double.IsFinite(lap.DurationMs) && lap.DurationMs > 0)
                .MinBy(lap => lap.DurationMs);
            if (fastestLap is not null)
            {
                _fastestLapTimesBySession[session.FilePath] = fastestLap.DurationMs;
                UpdateDateNodeHeaders(session);
            }

            foreach (var lap in laps.OrderBy(item => item.Index))
            {
                sessionNode.Items.Add(CreateLapTreeNode(
                    lap,
                    lap == fastestLap ? Brushes.LimeGreen : null));
            }

        }

        return laps;
    }

    private TreeViewItem CreateLapTreeNode(LapInfo lap, Brush? foreground = null)
    {
        var node = new TreeViewItem
        {
            Header = lap.TreeLabel,
            Tag = lap,
            FontWeight = FontWeights.Normal,
            ContextMenu = CreateLapContextMenu(lap)
        };
        if (foreground is not null)
        {
            node.Foreground = foreground;
        }

        return node;
    }

    private object FormatSessionHeader(
        SessionInfo session,
        double? fastestLapDurationMs,
        bool isFastestSessionOfDate)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Text = $"{session.SessionLabel}   ·   {TimeSpan.FromSeconds(session.SessionDurationSeconds):h\\:mm\\:ss}"
        });
        if (fastestLapDurationMs is not null)
        {
            var fastest = new TextBlock
            {
                Text = $"  ({FormatLapTime(fastestLapDurationMs.Value / 1000d)})",
                Cursor = Cursors.Hand,
                ToolTip = "Load the fastest lap"
            };
            if (isFastestSessionOfDate)
            {
                fastest.Foreground = Brushes.LimeGreen;
            }

            fastest.MouseLeftButtonDown += SessionFastestLap_MouseLeftButtonDown;
            header.Children.Add(fastest);
        }

        if (IsWetSession(session))
        {
            header.Children.Add(new TextBlock { Text = "  (wet)" });
        }

        return header;
    }

    private async void SessionFastestLap_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        DependencyObject? parent = sender as DependencyObject;
        while (parent is not null and not TreeViewItem)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }

        if (parent is not TreeViewItem { Tag: SessionInfo session } sessionNode)
        {
            return;
        }

        var laps = await PopulateLapNodesAsync(session, sessionNode);
        var fastestLap = laps?
            .Where(lap => !lap.IsAverage && double.IsFinite(lap.DurationMs) && lap.DurationMs > 0)
            .MinBy(lap => lap.DurationMs);
        var fastestNode = sessionNode.Items.OfType<TreeViewItem>()
            .FirstOrDefault(node => node.Tag is LapInfo lap && lap == fastestLap);
        if (fastestNode is null)
        {
            return;
        }

        if (fastestNode.IsSelected)
        {
            await SelectLapAsync((LapInfo)fastestNode.Tag);
        }
        else
        {
            fastestNode.IsSelected = true;
        }
    }

    private double GetFastestDuration(IEnumerable<SessionInfo> sessions) => sessions
        .Where(session => _fastestLapTimesBySession.ContainsKey(session.FilePath))
        .Select(session => _fastestLapTimesBySession[session.FilePath])
        .DefaultIfEmpty(double.PositiveInfinity)
        .Min();

    private bool IsFastestSessionOfDate(SessionInfo session, IEnumerable<SessionInfo> dateSessions)
    {
        var fastestSession = dateSessions
            .Where(candidate => _fastestLapTimesBySession.ContainsKey(candidate.FilePath))
            .MinBy(candidate => _fastestLapTimesBySession[candidate.FilePath]);
        return fastestSession is not null &&
               fastestSession.FilePath.Equals(session.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    private object CreateDateHeader(TrackDateGroup dateGroup, IReadOnlyList<SessionInfo> driverSessions)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(new TextBlock
        {
            Text = dateGroup.Date.ToString("ddd, MMM d, yyyy"),
            VerticalAlignment = VerticalAlignment.Center
        });

        var fastestDuration = GetFastestDuration(dateGroup.Sessions);
        if (double.IsFinite(fastestDuration))
        {
            var isOverallFastest = fastestDuration <= GetFastestDuration(driverSessions);
            var text = new TextBlock
            {
                Text = $"  ·  Fastest {FormatLapTime(fastestDuration / 1000d)}",
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (isOverallFastest)
            {
                text.Foreground = Brushes.LimeGreen;
            }

            header.Children.Add(text);
        }

        return header;
    }

    private void UpdateDateNodeHeaders(SessionInfo session)
    {
        foreach (var trackNode in SessionTree.Items.OfType<TreeViewItem>())
        foreach (var carNode in trackNode.Items.OfType<TreeViewItem>())
        foreach (var driverNode in carNode.Items.OfType<TreeViewItem>()
                     .Where(node => node.Tag is TrackDriverGroup driver &&
                                    driver.Sessions.Any(candidate =>
                                        candidate.FilePath.Equals(
                                            session.FilePath, StringComparison.OrdinalIgnoreCase))))
        {
            if (driverNode.Tag is not TrackDriverGroup driverGroup)
            {
                continue;
            }

            foreach (var dateNode in driverNode.Items.OfType<TreeViewItem>())
            {
                if (dateNode.Tag is TrackDateGroup dateGroup)
                {
                    dateNode.Header = CreateDateHeader(dateGroup, driverGroup.Sessions);
                    foreach (var sessionNode in dateNode.Items.OfType<TreeViewItem>()
                                 .Where(node => node.Tag is SessionInfo))
                    {
                        var dateSession = (SessionInfo)sessionNode.Tag;
                        var hasDuration = _fastestLapTimesBySession.TryGetValue(
                            dateSession.FilePath, out var duration);
                        sessionNode.Header = FormatSessionHeader(
                            dateSession,
                            hasDuration ? duration : null,
                            IsFastestSessionOfDate(dateSession, dateGroup.Sessions));
                    }
                }
            }
        }
    }

    private bool IsWetSession(SessionInfo session) =>
        GetSessionEdits(session).TrackCondition.Contains("WET", StringComparison.OrdinalIgnoreCase);

    private ContextMenu CreateVehicleContextMenu(TrackCarGroup car)
    {
        var menu = CreateThemedContextMenu();
        var aliases = car.Sessions.Select(session => session.VehicleAlias)
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sessionGuids = car.Sessions
            .Where(session => session.AiMData is not null)
            .Select(session => session.SessionGuid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        menu.Items.Add(CreateAssignVehicleMenuItem(
            "Assign vehicle name", aliases, sessionGuids, car.Sessions));
        return menu;
    }

    private MenuItem CreateAssignVehicleMenuItem(
        string header,
        string[] aliases,
        string[] sessionGuids,
        IReadOnlyList<SessionInfo> sessions)
    {
        var itemStyle = (Style)FindResource("DarkMenuItemStyle");
        var assignMenu = new MenuItem
        {
            Header = header,
            Style = itemStyle,
            ItemContainerStyle = itemStyle
        };
        foreach (var vehicleName in _vehicleCatalog.Names
                     .Concat(_customVehicleNames)
                     .Where(name => !IsVehicleHidden(name))
                     .Distinct(StringComparer.CurrentCultureIgnoreCase)
                     .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new MenuItem
            {
                Header = vehicleName,
                Style = itemStyle,
                Tag = new VehicleAssignment(aliases, sessionGuids, vehicleName, sessions),
                IsEnabled = aliases.Length > 0 || sessionGuids.Length > 0
            };
            item.Click += AssignVehicleName_Click;
            assignMenu.Items.Add(item);
        }

        if (assignMenu.Items.Count == 0)
        {
            assignMenu.Items.Add(new MenuItem
            {
                Header = "No catalog vehicles available",
                Style = itemStyle,
                IsEnabled = false
            });
        }

        return assignMenu;
    }

    private ContextMenu CreateThemedContextMenu()
    {
        var menuItemStyle = (Style)FindResource("DarkMenuItemStyle");
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("DarkContextMenuStyle"),
            ItemContainerStyle = menuItemStyle
        };
        menu.Resources[typeof(MenuItem)] = menuItemStyle;
        return menu;
    }

    private void AssignVehicleName_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: VehicleAssignment assignment })
        {
            return;
        }

        var updatedMappings = new Dictionary<string, string>(_vehicleMappings, StringComparer.OrdinalIgnoreCase);
        foreach (var alias in assignment.Aliases)
        {
            updatedMappings[alias] = assignment.VehicleName;
        }
        var updatedSessionMappings = new Dictionary<string, string>(
            _viewerSettings.SessionVehicleMappings, StringComparer.OrdinalIgnoreCase);
        foreach (var sessionGuid in assignment.SessionGuids)
        {
            updatedSessionMappings[sessionGuid] = assignment.VehicleName;
        }

        // A car-level assignment applies through the alias, so drop per-session overrides it would hide.
        foreach (var session in assignment.Sessions.Where(session => assignment.Aliases.Count > 0 &&
                     !string.IsNullOrWhiteSpace(session.VehicleAlias)))
        {
            updatedSessionMappings.Remove(session.SessionGuid);
        }

        var updatedSettings = new ViewerSettings
        {
            DistanceUnits = _viewerSettings.DistanceUnits,
            HiddenVehicleNames = [.. _viewerSettings.HiddenVehicleNames],
            CustomVehicleNames = _viewerSettings.CustomVehicleNames
                .Append(assignment.VehicleName)
                .Where(name => !_vehicleCatalog.Names.Contains(name, StringComparer.CurrentCultureIgnoreCase))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            VehicleMappings = updatedMappings,
            SessionVehicleMappings = updatedSessionMappings,
            ChannelVisibility = new Dictionary<string, bool>(
                _viewerSettings.ChannelVisibility, StringComparer.OrdinalIgnoreCase),
            ChannelOrder = [.. _viewerSettings.ChannelOrder],
            TrackMappings = new Dictionary<string, string>(
                _viewerSettings.TrackMappings, StringComparer.OrdinalIgnoreCase),
            SessionLayoutAssignments = new Dictionary<string, string>(
                _viewerSettings.SessionLayoutAssignments, StringComparer.OrdinalIgnoreCase)
        };
        var mappingPath = GetViewerSettingsPath();
        try
        {
            var parentDirectory = Path.GetDirectoryName(mappingPath);
            if (!string.IsNullOrWhiteSpace(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            File.WriteAllText(mappingPath, JsonSerializer.Serialize(updatedSettings, JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show(this,
                $"The vehicle mapping could not be saved to {mappingPath}.{Environment.NewLine}{exception.Message}",
                "Could not save vehicle mapping", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _vehicleMappings.Clear();
        foreach (var (alias, name) in updatedMappings)
        {
            _vehicleMappings[alias] = name;
        }
        _viewerSettings = updatedSettings;
        _customVehicleNames = updatedSettings.CustomVehicleNames;
        UpdateVehicleChoices();

        BuildSessionTree();
        var assignedNode = SessionTree.Items.OfType<TreeViewItem>()
            .Where(track => track.Tag is TrackLocation)
            .SelectMany(track => track.Items.OfType<TreeViewItem>()
                .Where(car => car.Tag is TrackCarGroup group &&
                              group.Sessions.Any(session => assignment.Sessions.Contains(session))))
            .FirstOrDefault();
        if (assignedNode is not null)
        {
            ExpandAncestors(assignedNode);
            assignedNode.IsSelected = true;
            assignedNode.BringIntoView();
        }

        StatusText.Text =
            $"Assigned {assignment.VehicleName} to {assignment.Aliases.Count + assignment.SessionGuids.Count} vehicle source(s).";
    }

    private sealed record VehicleAssignment(
        IReadOnlyList<string> Aliases,
        IReadOnlyList<string> SessionGuids,
        string VehicleName,
        IReadOnlyList<SessionInfo> Sessions);

    private async Task LoadFastestLapHeadersAsync(int generation)
    {
        IProgress<(SessionInfo Session, double? DurationMs)> progress =
            new Progress<(SessionInfo Session, double? DurationMs)>(result =>
        {
            if (generation != _treeGeneration)
            {
                return;
            }

            if (result.DurationMs is double durationMs)
            {
                _fastestLapTimesBySession[result.Session.FilePath] = durationMs;
            }

            UpdateDateNodeHeaders(result.Session);
            var node = FindSessionNode(result.Session);
            if (node is not null)
            {
                if (IsWetSession(result.Session))
                {
                    node.Foreground = Brushes.DeepSkyBlue;
                }
            }

            if (TrackOverviewPanel.Visibility == Visibility.Visible &&
                _selectedLocation is not null &&
                _overviewSessions.Any(session => session.FilePath.Equals(
                    result.Session.FilePath, StringComparison.OrdinalIgnoreCase)))
            {
                UpdateTrackStatistics(_selectedLocation, _overviewSessions, _overviewLapCounts);
            }
        });
        List<string> failures = [];

        await Task.Run(() =>
        {
            foreach (var session in _sessions)
            {
                try
                {
                    var durationMs = session.AiMData is { } aimData
                        ? aimData.Laps.MinBy(lap => lap.DurationMs)?.DurationMs
                        : TelemetryLoader.ReadFastestLapDurationMs(session.FilePath);
                    progress.Report((session, durationMs));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                   JsonException or InvalidDataException or ArgumentException)
                {
                    failures.Add($"{session.FileName}: {exception.Message}");
                }
            }
        });

        if (generation == _treeGeneration && failures.Count > 0)
        {
            MessageBox.Show(this,
                $"Fastest-lap summaries could not be read for {failures.Count} sessions. " +
                $"The first error was: {failures[0]}",
                "Some fastest-lap times are unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void TrackLapLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LapNavigationTarget target })
        {
            return;
        }

        var sessionNode = FindSessionNode(target.Session);
        if (sessionNode is null)
        {
            MessageBox.Show(this, "The source session is no longer in the current library.",
                "Lap is unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExpandAncestors(sessionNode);
        var laps = await PopulateLapNodesAsync(target.Session, sessionNode);
        if (laps is null)
        {
            return;
        }

        var lap = target.IsOptimal
            ? TelemetryLoader.CreateOptimalLap(target.Session)
            : laps.Where(candidate => !candidate.IsAverage)
                .MinBy(candidate => candidate.DurationMs);
        if (lap is null)
        {
            MessageBox.Show(this, "No matching lap telemetry was found for this session.",
                "Lap is unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var lapNode = sessionNode.Items.OfType<TreeViewItem>()
            .FirstOrDefault(node => node.Tag is LapInfo candidate &&
                                    candidate.IsOptimal == target.IsOptimal &&
                                    (target.IsOptimal || candidate.Index == lap.Index));
        if (lapNode is null)
        {
            MessageBox.Show(this, "The lap could not be found in the session tree.",
                "Lap is unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExpandAncestors(lapNode);
        lapNode.IsSelected = true;
        lapNode.BringIntoView();
        await SelectLapAsync(lap);
    }

    private static void ExpandAncestors(TreeViewItem node)
    {
        ItemsControl? parent = ItemsControl.ItemsControlFromItemContainer(node);
        while (parent is TreeViewItem parentNode)
        {
            parentNode.IsExpanded = true;
            parent = ItemsControl.ItemsControlFromItemContainer(parentNode);
        }
    }

    private ContextMenu CreateLapContextMenu(LapInfo lap)
    {
        var menu = CreateThemedContextMenu();
        var itemStyle = (Style)FindResource("DarkMenuItemStyle");
        var setReference = new MenuItem
        {
            Header = "Use as reference lap",
            Style = itemStyle,
            Tag = lap
        };
        setReference.Click += SetReferenceLap_Click;
        menu.Items.Add(setReference);

        var clearReference = new MenuItem
        {
            Header = "Clear reference lap",
            Style = itemStyle,
            IsEnabled = _referenceLap is not null
        };
        clearReference.Click += ClearReferenceLap_Click;
        menu.Items.Add(clearReference);
        menu.Opened += (_, _) => clearReference.IsEnabled = _referenceLap is not null;

        return menu;
    }

    private ContextMenu CreateSessionReferenceContextMenu(SessionInfo session)
    {
        var menu = CreateThemedContextMenu();
        var item = new MenuItem
        {
            Header = "Use fastest lap as reference",
            Style = (Style)FindResource("DarkMenuItemStyle"),
            Tag = session
        };
        item.Click += UseSessionFastestAsReference_Click;
        menu.Items.Add(item);
        var detailsItem = new MenuItem
        {
            Header = "Session details…",
            Style = (Style)FindResource("DarkMenuItemStyle")
        };
        detailsItem.Click += (_, _) => ShowSessionDetailsDialog(session);
        menu.Items.Add(detailsItem);
        menu.Items.Add(CreateAssignVehicleMenuItem(
            "Assign vehicle to this session", [], [session.SessionGuid], [session]));
        menu.Items.Add(CreateExportMenuItem([session], session.LocalStart.ToString("yyyy-MM-dd HH.mm")));
        return menu;
    }

    private async void UseSessionFastestAsReference_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: var tag })
        {
            return;
        }

        IReadOnlyList<SessionInfo> targetSessions = tag switch
        {
            SessionInfo single => [single],
            TrackDateGroup dateGroup => dateGroup.Sessions,
            _ => []
        };
        if (targetSessions.Count == 0)
        {
            return;
        }

        try
        {
            var candidates = new List<LapInfo>();
            foreach (var session in targetSessions)
            {
                if (!_lapsBySession.TryGetValue(session.FilePath, out var laps))
                {
                    var sessionToRead = session;
                    laps = await Task.Run(() => TelemetryLoader.ReadLaps(sessionToRead));
                    _lapsBySession[session.FilePath] = laps;
                }

                candidates.AddRange(laps);
            }

            var fastestLap = candidates
                .Where(lap => !lap.IsAverage && double.IsFinite(lap.DurationMs) && lap.DurationMs > 0)
                .MinBy(lap => lap.DurationMs);
            if (fastestLap is null)
            {
                MessageBox.Show(this, "No fastest driven lap was found.",
                    "Reference lap unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await SetReferenceLapAsync(fastestLap);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           JsonException or InvalidDataException or ArgumentException)
        {
            MessageBox.Show(this, exception.Message, "Reference lap unavailable",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SetReferenceLap_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: LapInfo lap })
        {
            return;
        }

        await SetReferenceLapAsync(lap);
    }

    private async Task SetReferenceLapAsync(LapInfo lap)
    {
        if (_primaryLap is not null &&
            (!_trackBySession.TryGetValue(_primaryLap.Session.FilePath, out var primaryTrack) ||
             !_trackBySession.TryGetValue(lap.Session.FilePath, out var referenceTrack) ||
             !ReferenceEquals(primaryTrack, referenceTrack)))
        {
            MessageBox.Show(this, "Choose a reference lap from the same track as the selected lap.",
                "Reference must be from the same track", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _referenceLap = lap;
        UpdateReferenceHeader();
        if (_primaryLap is not null)
        {
            await LoadSelectedTracesAsync();
        }
    }

    private async void ClearReferenceLap_Click(object sender, RoutedEventArgs e)
    {
        _referenceLap = null;
        _referenceTrace = null;
        UpdateReferenceHeader();
        if (_primaryLap is not null)
        {
            await LoadSelectedTracesAsync();
        }
    }

    private async Task SelectLapAsync(LapInfo lap)
    {
        _primaryLap = lap;
        _selectedSession = lap.Session;
        _selectedLocation = _trackBySession[lap.Session.FilePath];
        DataViewPanel.Visibility = Visibility.Visible;
        TrackOverviewPanel.Visibility = Visibility.Collapsed;
        TrackTitleText.Text = _selectedLocation.DisplayName;

        if (_referenceLap is not null &&
            (!_trackBySession.TryGetValue(_referenceLap.Session.FilePath, out var referenceTrack) ||
             !ReferenceEquals(referenceTrack, _selectedLocation)))
        {
            _referenceLap = null;
            _referenceTrace = null;
            UpdateReferenceHeader();
        }

        await LoadSelectedTracesAsync();
    }

    private async Task LoadSelectedTracesAsync()
    {
        if (_primaryLap is null)
        {
            return;
        }

        var generation = ++_loadGeneration;
        var primary = _primaryLap;
        var reference = _referenceLap;
        StatusText.Text = $"Loading {primary.Label} telemetry…";
        BeginLoading($"Loading {primary.Session.FileName} · {primary.Label}");

        try
        {
            var primaryTask = Task.Run(() => TelemetryLoader.ReadTrace(primary));
            var referenceTask = reference is null
                ? Task.FromResult<SessionTrace?>(null)
                : Task.Run<SessionTrace?>(() => TelemetryLoader.ReadTrace(reference));
            await Task.WhenAll((Task)primaryTask, referenceTask);

            if (generation != _loadGeneration)
            {
                return;
            }

            _primaryTrace = primaryTask.Result;
            _referenceTrace = referenceTask.Result;
            _segmentMap = await LoadSegmentMapAsync(GetMeanlinePath(primary.Session));
            if (generation != _loadGeneration)
            {
                return;
            }

            _cursorDistanceM = 0;
            UpdateCharts();
            UpdateStatistics();
            StatusText.Text =
                $"Showing {_primaryTrace.Points.Count:N0} samples from {primary.Label}. " +
                $"Speed is converted to {SpeedChart.Unit}; acceleration axes are shown in g.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           JsonException or InvalidDataException or ArgumentException)
        {
            if (generation != _loadGeneration)
            {
                return;
            }

            _primaryTrace = null;
            _referenceTrace = null;
            UpdateCharts();
            UpdateStatistics();
            StatusText.Text = "Could not load lap telemetry.";
            MessageBox.Show(this, exception.Message, "Lap could not be loaded",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndLoading();
        }
    }

    private string? GetMeanlinePath(SessionInfo session) =>
        session.MeanlinePath ??
        (_trackBySession.TryGetValue(session.FilePath, out var location) ? location.MeanlinePath : null);

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

    private void UpdateCharts()
    {
        if (_primaryTrace is null)
        {
            foreach (var chart in _chartElements.Values)
            {
                chart.Lines = [];
                chart.SegmentBoundariesM = [];
            }

            TrackMapView.SegmentMap = null;
            TrackMapView.Traces = [];
            return;
        }

        var traces = new List<SessionTrace> { _primaryTrace };
        if (_referenceTrace is not null)
        {
            traces.Add(_referenceTrace);
        }

        var maxDistance = _primaryTrace.DistanceM;
        var scaledSegments = _segmentMap is not null && maxDistance > 0
            ? _segmentMap.ScaledTo(maxDistance)
            : null;
        TrackMapView.SegmentMap = scaledSegments;
        TrackMapView.Traces = traces;
        foreach (var channel in BuildChartChannelSettings())
        {
            var chart = GetOrCreateChart(channel);
            chart.MaxDistanceM = maxDistance;
            chart.SegmentBoundariesM = scaledSegments?.BoundariesM ?? [];
            chart.Lines = channel.Key switch
            {
                "speed" => traces.Select((trace, index) => new ChartLine(
                    index == 0 ? "Session" : "Reference",
                    trace.Points.Select(point => new ChartValue(
                        point.DistanceM,
                        point.SpeedMps * (UseImperialDistance ? 2.236936292 : 3.6))).ToArray(),
                    index == 0 ? PrimaryColor : ReferenceColor)).ToArray(),
                "combined-g" => BuildCombinedGForceLines(traces),
                "longitudinal-g" => BuildTelemetryLines(traces,
                    point => point.LongitudinalG, "longitudinal"),
                "lateral-g" => BuildTelemetryLines(traces,
                    point => point.LateralG, "lateral"),
                "delta-time" => _referenceTrace is null
                    ? []
                    : [new ChartLine("A − B", _primaryTrace.Points.Select(point =>
                        new ChartValue(
                            point.DistanceM,
                            point.DistanceM <= _referenceTrace.DistanceM
                                ? (point.TimeMs - TelemetryLoader.InterpolateTimeMs(
                                    _referenceTrace.Points, point.DistanceM)) / 1000d
                                : double.NaN)).ToArray(), PrimaryColor)],
                _ when channel.Key.StartsWith("aim:", StringComparison.OrdinalIgnoreCase) =>
                    BuildAiMChannelLines(traces, channel.Key["aim:".Length..]),
                _ => []
            };
        }

        SetCursorDistance(_cursorDistanceM);
        UpdateReferenceHeader();
    }

    private static IReadOnlyList<ChartLine> BuildCombinedGForceLines(IReadOnlyList<SessionTrace> traces)
    {
        var lines = new List<ChartLine>();
        foreach (var (trace, index) in traces.Select((trace, index) => (trace, index)))
        {
            var label = index == 0 ? "Session" : "Reference";
            var color = index == 0 ? PrimaryColor : ReferenceColor;
            lines.Add(new ChartLine($"{label} lateral", trace.Points
                .Select(point => new ChartValue(point.DistanceM, point.LateralG)).ToArray(), color,
                DeltaKey: "lateral"));
            lines.Add(new ChartLine($"{label} longitudinal", trace.Points
                .Select(point => new ChartValue(point.DistanceM, point.LongitudinalG)).ToArray(),
                color, Dashed: true, DeltaKey: "longitudinal"));
        }

        return lines;
    }

    private static IReadOnlyList<ChartLine> BuildTelemetryLines(
        IReadOnlyList<SessionTrace> traces,
        Func<TelemetryPoint, double> getValue,
        string label)
    {
        return traces.Select((trace, index) => new ChartLine(
                $"{(index == 0 ? "Session" : "Reference")} {label}",
                trace.Points.Select(point => new ChartValue(point.DistanceM, getValue(point))).ToArray(),
                index == 0 ? PrimaryColor : ReferenceColor,
                DeltaKey: label))
            .ToArray();
    }

    private static IReadOnlyList<ChartLine> BuildAiMChannelLines(
        IReadOnlyList<SessionTrace> traces,
        string channelName)
    {
        return traces.Select((trace, index) => new ChartLine(
                index == 0 ? "Session" : "Reference",
                trace.GetChannelValues(channelName),
                index == 0 ? PrimaryColor : ReferenceColor))
            .ToArray();
    }

    private void UpdateStatistics()
    {
        UpdateLapTypeTag(PrimaryLapTypeText, _primaryLap);
        UpdateLapTypeTag(ReferenceLapTypeText, _referenceLap);
        PrimaryLapDetailsText.Text = _primaryLap is null
            ? string.Empty
            : $"{GetVehicleName(_primaryLap.Session)}  ·  " +
              $"{GetSessionEdits(_primaryLap.Session).TrackCondition}";
        ReferenceLapDetailsText.Text = _referenceLap is null
            ? "Right-click a lap to set reference"
            : $"{GetVehicleName(_referenceLap.Session)}  ·  " +
              $"{GetSessionEdits(_referenceLap.Session).TrackCondition}";
        PrimaryLapText.Text = _primaryLap is null
            ? "—"
            : FormatLapSummary(_primaryLap, _primaryTrace?.LapDurationSeconds ??
                _primaryLap.DurationMs / 1000d);
        ReferenceLapText.Text = _referenceLap is null
            ? "—"
            : FormatLapSummary(_referenceLap, _referenceTrace?.LapDurationSeconds ??
                _referenceLap.DurationMs / 1000d);
        if (_primaryTrace is null)
        {
            LapDeltaText.Text = "—";
            LapDeltaText.Foreground = Brushes.White;
            return;
        }

        if (_referenceTrace is null)
        {
            LapDeltaText.Text = "—";
            LapDeltaText.Foreground = Brushes.White;
            return;
        }

        var delta = _primaryTrace.LapDurationSeconds - _referenceTrace.LapDurationSeconds;
        LapDeltaText.Text = $"{(delta > 0 ? "+" : string.Empty)}{delta:0.000}s";
        LapDeltaText.Foreground = delta <= 0 ? Brushes.LightGreen : Brushes.Orange;
    }

    private string FormatLapSummary(LapInfo lap, double durationSeconds)
    {
        var lapDate = lap.Session.LocalStart.AddMilliseconds(lap.StartTimeSessionMs);
        return $"{GetDriverName(lap.Session)}  ·  {lapDate:MMM d, yyyy}  ·  " +
               $"{lap.Label}  ·  {FormatLapTime(durationSeconds)}";
    }

    private void UpdateLapTypeTag(TextBlock tag, LapInfo? lap)
    {
        if (lap is null)
        {
            tag.Text = string.Empty;
            tag.Visibility = Visibility.Collapsed;
            return;
        }

        if (lap.IsOptimal)
        {
            tag.Text = "Optimal";
            tag.Foreground = Brushes.MediumPurple;
            tag.Visibility = Visibility.Visible;
            return;
        }

        if (_lapsBySession.TryGetValue(lap.Session.FilePath, out var laps) &&
            laps.Where(candidate => !candidate.IsAverage).MinBy(candidate => candidate.DurationMs) is { } fastest &&
            fastest.Index == lap.Index)
        {
            tag.Text = "Fastest";
            tag.Foreground = Brushes.LimeGreen;
            tag.Visibility = Visibility.Visible;
            return;
        }

        tag.Text = string.Empty;
        tag.Visibility = Visibility.Collapsed;
    }

    private void UpdateReferenceHeader()
    {
        UpdateStatistics();
    }

    private void Chart_DistanceHovered(double distanceM) => SetCursorDistance(distanceM);

    private void SetCursorDistance(double distanceM)
    {
        _cursorDistanceM = distanceM;
        foreach (var chart in _chartElements.Values)
        {
            chart.CursorDistanceM = distanceM;
        }
        TrackMapView.CursorDistanceM = distanceM;

        if (_primaryTrace is null)
        {
            CursorDistanceText.Text = "—";
            return;
        }

        var primaryDistance = Math.Clamp(distanceM, 0, _primaryTrace.DistanceM);
        var deltaText = string.Empty;
        if (_referenceTrace is not null && primaryDistance <= _referenceTrace.DistanceM)
        {
            var primaryTime = TelemetryLoader.InterpolateTimeMs(_primaryTrace.Points, primaryDistance);
            var referenceTime = TelemetryLoader.InterpolateTimeMs(_referenceTrace.Points, primaryDistance);
            var delta = (primaryTime - referenceTime) / 1000d;
            deltaText = $"   ·   Δ {(delta > 0 ? "+" : string.Empty)}{delta:0.000}s";
        }

        var cursorLabel = UseImperialDistance
            ? $"{primaryDistance * 3.28084:0} ft"
            : $"{primaryDistance:0} m";
        CursorDistanceText.Text = $"{cursorLabel}{deltaText}";
    }

    private async Task ShowTrackOverviewAsync(
        TrackLocation location,
        IReadOnlyList<SessionInfo>? visibleSessions,
        string? groupName = null)
    {
        var generation = ++_overviewGeneration;
        DataViewPanel.Visibility = Visibility.Collapsed;
        TrackOverviewPanel.Visibility = Visibility.Visible;
        var sessions = (visibleSessions ?? location.Sessions)
            .Where(session => !IsVehicleHidden(GetVehicleName(session)))
            .ToArray();
        _selectedLocation = location;
        _overviewSessions = sessions;
        _overviewLapCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        OverviewTrackTitle.Text = groupName is null
            ? location.DisplayName
            : $"{location.DisplayName}  /  {groupName}";
        UpdateTrackOverviewDistanceLabels(location);
        OverviewVehicleCount.Text = sessions.Select(GetVehicleName)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).Count().ToString("N0");
        OverviewSessionCount.Text = sessions.Length.ToString("N0");
        OverviewLapCount.Text = "Counting…";
        OverviewStatusText.Text = "Loading the reference line and session statistics…";
        TrackCarStatsList.ItemsSource = Array.Empty<TrackDriverStats>();
        BeginLoading($"Loading {location.DisplayName}");

        try
        {
            IReadOnlyList<TrackPoint> meanlinePoints = [];
            var meanlinePath = location.MeanlinePath;
            if (meanlinePath is not null)
            {
                if (_meanlineCache.TryGetValue(meanlinePath, out var cachedPoints))
                {
                    meanlinePoints = cachedPoints;
                }
                else
                {
                    meanlinePoints = await Task.Run(() => TelemetryLoader.ReadMeanlinePoints(meanlinePath));
                    _meanlineCache[meanlinePath] = meanlinePoints;
                }
            }

            if (generation != _overviewGeneration)
            {
                return;
            }

            var segmentMap = await LoadSegmentMapAsync(meanlinePath);
            if (generation != _overviewGeneration)
            {
                return;
            }

            TrackOverviewMap.SegmentMap = segmentMap;
            TrackOverviewMap.MeanlinePoints = meanlinePoints;
            TrackOverviewMap.Traces = [];

            var progress = new Progress<int>(completed =>
            {
                if (generation == _overviewGeneration)
                {
                    OverviewStatusText.Text = $"Counting laps from session files… {completed:N0}/{location.Sessions.Count:N0}";
                    UpdateLoadingDetail(
                        $"Counting laps · {location.DisplayName} · {completed:N0}/{location.Sessions.Count:N0}");
                }
            });
            var lapCounts = await GetTrackLapCountsAsync(location, progress);
            if (generation != _overviewGeneration)
            {
                return;
            }

            _overviewLapCounts = lapCounts;
            UpdateTrackStatistics(location, sessions, lapCounts);
            StatusText.Text = $"Showing track overview for {location.DisplayName}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           JsonException or InvalidDataException or ArgumentException)
        {
            if (generation != _overviewGeneration)
            {
                return;
            }

            OverviewLapCount.Text = "Unavailable";
            OverviewStatusText.Text = "Track data could not be fully loaded.";
            StatusText.Text = "Could not load track overview.";
            _trackStatsTasks.Remove(location.Key);
            MessageBox.Show(this, exception.Message, "Track overview could not be loaded",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndLoading();
        }
    }

    private Task<IReadOnlyDictionary<string, int>> GetTrackLapCountsAsync(
        TrackLocation location,
        IProgress<int> progress)
    {
        if (_trackStatsTasks.TryGetValue(location.Key, out var existing))
        {
            return existing;
        }

        var task = Task.Run<IReadOnlyDictionary<string, int>>(() =>
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var completed = 0;
            foreach (var session in location.Sessions)
            {
                var count = session.AiMData?.Laps.Count ?? TelemetryLoader.ReadLapCount(session.FilePath);
                result[session.FilePath] = count;
                progress.Report(++completed);
            }

            return result;
        });
        _trackStatsTasks[location.Key] = task;
        return task;
    }

    private void UpdateTrackStatistics(
        TrackLocation location,
        IReadOnlyList<SessionInfo> visibleSessions,
        IReadOnlyDictionary<string, int> lapCounts)
    {
        var vehicleGroups = visibleSessions.GroupBy(GetVehicleName, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var rows = visibleSessions
            .GroupBy(GetDriverName, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(driverGroup =>
            {
                var driverSessions = driverGroup.ToArray();
                var driverLaps = driverSessions.Sum(session =>
                    lapCounts.TryGetValue(session.FilePath, out var count) ? count : 0);
                var cars = driverSessions
                    .GroupBy(GetVehicleName, StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(carGroup =>
                    {
                        var sessions = carGroup.ToArray();
                        var fastestLapSession = sessions
                            .Where(session => _fastestLapTimesBySession.ContainsKey(session.FilePath))
                            .MinBy(session => _fastestLapTimesBySession[session.FilePath]);
                        var optimalLap = sessions
                            .Select(TelemetryLoader.CreateOptimalLap)
                            .OfType<LapInfo>()
                            .Where(lap => double.IsFinite(lap.DurationMs) && lap.DurationMs > 0)
                            .MinBy(lap => lap.DurationMs);
                        return new TrackCarStats(
                            carGroup.Key,
                            sessions.Length,
                            fastestLapSession is null
                                ? null
                                : new LapNavigationTarget(
                                    fastestLapSession,
                                    _fastestLapTimesBySession[fastestLapSession.FilePath],
                                    false),
                            optimalLap is null
                                ? null
                                : new LapNavigationTarget(optimalLap.Session, optimalLap.DurationMs, true),
                            sessions.Sum(session =>
                                lapCounts.TryGetValue(session.FilePath, out var count) ? count : 0));
                    })
                    .ToArray();
                return new TrackDriverStats(driverGroup.Key, driverLaps.ToString("N0"), cars);
            })
            .ToArray();

        TrackCarStatsList.ItemsSource = rows;
        OverviewVehicleCount.Text = vehicleGroups.Length.ToString("N0");
        OverviewSessionCount.Text = visibleSessions.Count.ToString("N0");
        OverviewLapCount.Text = visibleSessions.Sum(session =>
            lapCounts.TryGetValue(session.FilePath, out var count) ? count : 0).ToString("N0");
        OverviewStatusText.Text =
            $"{location.DisplayName} · {visibleSessions.Count:N0} sessions · {vehicleGroups.Length:N0} vehicles";
    }

    private void ShowSessionDetailsDialog(SessionInfo session)
    {
        var edits = GetSessionEdits(session);
        var vehicleDescription = string.IsNullOrWhiteSpace(session.VehicleAlias)
            ? string.IsNullOrWhiteSpace(session.AiMData?.VehicleName)
                ? "No vehicle alias was included in the session metadata."
                : $"AiM vehicle: {session.AiMData.VehicleName}"
            : _vehicleCatalog.ResolveAlias(session.VehicleAlias) is null
                ? $"No catalog vehicle ID matches alias {session.VehicleAlias}. Select a car above."
                : $"Vehicle alias: {session.VehicleAlias}";
        var driverDescription = !string.IsNullOrWhiteSpace(session.GarminGuid)
            ? $"Garmin profile: {session.GarminGuid}"
            : string.IsNullOrWhiteSpace(session.AiMData?.DriverName)
                ? "No driver profile ID was included in the session metadata."
                : $"AiM driver: {session.AiMData.DriverName}";
        var dialog = new SessionDetailsDialog(
            $"{(_trackBySession.TryGetValue(session.FilePath, out var sessionTrack) ? sessionTrack.DisplayName : session.MeanlineTrackName)}  ·  {session.LocalStart:MMM d, yyyy  h:mm tt}",
            _vehicleChoices,
            BuildSessionLayoutChoices(session),
            _viewerSettings.SessionLayoutAssignments.TryGetValue(session.SessionGuid, out var layoutAssignment)
                ? layoutAssignment
                : UseSourceLayout,
            edits,
            $"{vehicleDescription} · {driverDescription}",
            details => SaveSessionDetails(session, details))
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private bool SaveSessionDetails(SessionInfo session, SessionDetailsResult details)
    {
        var vehicleName = details.VehicleName;
        var trackCondition = details.TrackCondition;
        var conditionChanged = !trackCondition.Equals(
            GetSessionEdits(session).TrackCondition, StringComparison.OrdinalIgnoreCase);
        var metadataNeedsUpdate = !trackCondition.Equals(
            session.TrackCondition, StringComparison.OrdinalIgnoreCase);
        if (session.AiMData is null && metadataNeedsUpdate &&
            string.IsNullOrWhiteSpace(session.SessionMetadataPath) &&
            conditionChanged)
        {
            MessageBox.Show(this,
                "This session has no metadata file to update, so its track condition cannot be changed.",
                "Session metadata unavailable", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
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
                MessageBox.Show(this,
                    $"The track condition could not be saved to {session.SessionMetadataPath}.{Environment.NewLine}{exception.Message}",
                    "Could not save track condition", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        var updatedLayoutAssignments = new Dictionary<string, string>(
            _viewerSettings.SessionLayoutAssignments, StringComparer.OrdinalIgnoreCase);
        var selectedLayout = details.LayoutValue;
        if (string.IsNullOrWhiteSpace(selectedLayout) ||
            selectedLayout.Equals(UseSourceLayout, StringComparison.OrdinalIgnoreCase))
        {
            updatedLayoutAssignments.Remove(session.SessionGuid);
        }
        else
        {
            updatedLayoutAssignments[session.SessionGuid] = selectedLayout;
        }

        if (!updatedLayoutAssignments.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(_viewerSettings.SessionLayoutAssignments
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)))
        {
            var updatedSettings = new ViewerSettings
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
                TrackMappings = new Dictionary<string, string>(
                    _viewerSettings.TrackMappings, StringComparer.OrdinalIgnoreCase),
                SessionLayoutAssignments = updatedLayoutAssignments
            };
            if (!SaveViewerSettings(
                    updatedSettings, "Could not save track assignment", "The session's track assignment could not be saved to"))
            {
                return false;
            }

            _viewerSettings = updatedSettings;
            BuildTrackLocations();
        }

        _sessionEdits[session.SessionGuid] = new SessionEdits(
            vehicleName,
            trackCondition,
            details.RaceType,
            details.DriverName);
        if (!SaveSessionEdits())
        {
            return false;
        }

        BuildSessionTree();
        var node = FindSessionNode(session);
        if (node is not null)
        {
            ExpandAncestors(node);
            node.IsSelected = true;
            node.BringIntoView();
        }

        StatusText.Text = "Session vehicle and conditions saved.";
        return true;
    }

    private TreeViewItem? FindSessionNode(SessionInfo session)
    {
        foreach (var track in SessionTree.Items.OfType<TreeViewItem>())
        foreach (var car in track.Items.OfType<TreeViewItem>())
        foreach (var driver in car.Items.OfType<TreeViewItem>().Where(node => node.Tag is TrackDriverGroup))
        foreach (var date in driver.Items.OfType<TreeViewItem>())
        foreach (var node in date.Items.OfType<TreeViewItem>())
        {
            if (node.Tag is SessionInfo candidate && candidate.SessionGuid == session.SessionGuid)
            {
                return node;
            }
        }

        return null;
    }

    private string GetVehicleName(SessionInfo session)
    {
        if (_viewerSettings.SessionVehicleMappings.TryGetValue(session.SessionGuid, out var mappedName))
        {
            return mappedName;
        }

        if (!string.IsNullOrWhiteSpace(session.VehicleAlias) &&
            _vehicleMappings.TryGetValue(session.VehicleAlias, out mappedName))
        {
            return mappedName;
        }

        if (_sessionEdits.TryGetValue(session.SessionGuid, out var edits) &&
            !string.IsNullOrWhiteSpace(edits.VehicleName))
        {
            return edits.VehicleName;
        }

        return !string.IsNullOrWhiteSpace(session.AiMData?.VehicleName)
            ? session.AiMData.VehicleName
            : _vehicleCatalog.ResolveAlias(session.VehicleAlias) ?? session.DefaultVehicleName;
    }

    private bool IsVehicleHidden(string vehicleName) =>
        _viewerSettings.HiddenVehicleNames.Contains(vehicleName, StringComparer.CurrentCultureIgnoreCase);

    private SessionEdits GetSessionEdits(SessionInfo session) =>
        _sessionEdits.TryGetValue(session.SessionGuid, out var edits)
            ? edits with
            {
                VehicleName = GetVehicleName(session),
                TrackCondition = string.IsNullOrWhiteSpace(edits.TrackCondition)
                    ? string.IsNullOrWhiteSpace(session.TrackCondition) ? "UNKNOWN" : session.TrackCondition
                    : edits.TrackCondition,
                RaceType = string.IsNullOrWhiteSpace(edits.RaceType)
                    ? string.IsNullOrWhiteSpace(session.RaceType) ? "TRACK" : session.RaceType
                    : edits.RaceType,
                DriverName = string.IsNullOrWhiteSpace(edits.DriverName)
                    ? GetProfileDriverName(session)
                    : edits.DriverName
            }
            : new SessionEdits(
                GetVehicleName(session),
                string.IsNullOrWhiteSpace(session.TrackCondition) ? "UNKNOWN" : session.TrackCondition,
                string.IsNullOrWhiteSpace(session.RaceType) ? "TRACK" : session.RaceType,
                GetProfileDriverName(session));

    private string GetDriverName(SessionInfo session) => GetSessionEdits(session).DriverName;

    private string GetProfileDriverName(SessionInfo session) =>
        _driverNames.TryGetValue(session.GarminGuid, out var driverName)
            ? driverName
            : string.IsNullOrWhiteSpace(session.AiMData?.DriverName)
                ? "Unknown driver"
                : session.AiMData.DriverName;

    private IReadOnlyList<SessionLayoutChoice> BuildSessionLayoutChoices(SessionInfo session)
    {
        var trackName = _trackBySession.TryGetValue(session.FilePath, out var location)
            ? location.TrackName
            : session.MeanlineTrackName;
        var layouts = _sessions
            .Where(candidate =>
                DistanceMeters(session.Latitude, session.Longitude, candidate.Latitude, candidate.Longitude) <=
                TrackMergeDistanceM &&
                (string.IsNullOrWhiteSpace(session.MeanlineTrackName) ||
                 string.IsNullOrWhiteSpace(candidate.MeanlineTrackName) ||
                 candidate.MeanlineTrackName.Equals(
                     session.MeanlineTrackName, StringComparison.CurrentCultureIgnoreCase)))
            .Select(GetEffectiveLayoutName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var choices = new List<SessionLayoutChoice>
        {
            new(
                string.IsNullOrWhiteSpace(session.MeanlineConfigurationName) ||
                session.MeanlineConfigurationName.Equals(
                    "Layout unspecified", StringComparison.OrdinalIgnoreCase)
                    ? "Use data layout (unspecified)"
                    : $"Use data layout ({session.MeanlineConfigurationName.Trim()})",
                UseSourceLayout)
        };
        choices.AddRange(layouts.Select(name => new SessionLayoutChoice(
            string.IsNullOrWhiteSpace(trackName) ? name : $"{trackName} · {name}",
            name)));
        if (layouts.Length > 1 ||
            string.IsNullOrWhiteSpace(session.MeanlineConfigurationName) ||
            session.MeanlineConfigurationName.Equals("Layout unspecified", StringComparison.OrdinalIgnoreCase) ||
            _viewerSettings.SessionLayoutAssignments.TryGetValue(session.SessionGuid, out var assignment) &&
            assignment.Equals(UnspecifiedLayout, StringComparison.OrdinalIgnoreCase))
        {
            choices.Add(new SessionLayoutChoice(
                string.IsNullOrWhiteSpace(trackName)
                    ? "Layout unspecified"
                    : $"{trackName} · Layout unspecified",
                UnspecifiedLayout));
        }

        return choices;
    }

    private void LoadSessionEdits()
    {
        if (!File.Exists(_sessionEditsPath))
        {
            return;
        }

        try
        {
            var savedEdits = JsonSerializer.Deserialize<Dictionary<string, SessionEdits>>(
                File.ReadAllText(_sessionEditsPath));
            if (savedEdits is not null)
            {
                foreach (var (key, value) in savedEdits)
                {
                    if (!string.IsNullOrWhiteSpace(value.VehicleName))
                    {
                        _sessionEdits[key] = value;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show(this,
                $"Saved session corrections could not be loaded. Original session metadata will be used.{Environment.NewLine}{exception.Message}",
                "Session corrections could not be loaded", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool SaveSessionEdits()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_sessionEditsPath)!);
            File.WriteAllText(_sessionEditsPath, JsonSerializer.Serialize(_sessionEdits, JsonOptions));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show(this, $"Session corrections could not be saved.{Environment.NewLine}{exception.Message}",
                "Could not save session details", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private static double DistanceMeters(double latitudeA, double longitudeA, double latitudeB, double longitudeB)
    {
        const double earthRadiusM = 6_371_000;
        var latitudeDelta = DegreesToRadians(latitudeB - latitudeA);
        var longitudeDelta = DegreesToRadians(longitudeB - longitudeA);
        var a = Math.Pow(Math.Sin(latitudeDelta / 2), 2) +
                Math.Cos(DegreesToRadians(latitudeA)) * Math.Cos(DegreesToRadians(latitudeB)) *
                Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        return earthRadiusM * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;

    private static string? FindDefaultDataFolder()
    {
        var rememberedFolderPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TrackTelemetryViewer",
            "last-data-folder.txt");
        if (File.Exists(rememberedFolderPath))
        {
            var rememberedFolder = File.ReadAllText(rememberedFolderPath).Trim();
            if (Directory.Exists(rememberedFolder) &&
                TelemetryLoader.HasSessionFiles(rememberedFolder))
            {
                return rememberedFolder;
            }
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Data"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Data")),
            Path.Combine(Environment.CurrentDirectory, "Data")
        };

        return candidates.FirstOrDefault(candidate =>
            Directory.Exists(candidate) &&
            TelemetryLoader.HasSessionFiles(candidate));
    }

    private static string FormatLapTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}";
    }
}
