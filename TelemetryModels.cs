using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace TrackTelemetryViewer;

internal sealed record SessionInfo(
    string FilePath,
    string FileName,
    string SessionGuid,
    string GarminGuid,
    string? SessionMetadataPath,
    double Latitude,
    double Longitude,
    DateTime LocalStart,
    double SessionDurationSeconds,
    string VehicleAlias,
    string TrackCondition,
    string RaceType,
    string? MeanlinePath,
    string MeanlineTrackName,
    string MeanlineConfigurationName,
    double MeanlineLengthM,
    string? OptimalLapPath,
    double OptimalLapDurationMs,
    double OptimalLapStartTimeSessionMs,
    AiMSessionData? AiMData = null)
{
    public string VehicleAssignmentKey => Path.GetFullPath(FilePath);
    public string SessionLabel => LocalStart.ToString("h:mm tt");
    public string DateLabel => LocalStart.ToString("MMMM d, yyyy");
    public string CompareLabel => $"{LocalStart:MMM d, yyyy  h:mm tt}";
    public string DefaultVehicleName => string.IsNullOrWhiteSpace(VehicleAlias)
        ? "Unassigned vehicle"
        : $"Unassigned · {VehicleAlias[..Math.Min(VehicleAlias.Length, 8)].ToUpperInvariant()}";
}

internal sealed record AiMSessionData(
    string DriverName,
    string VehicleName,
    string TrackName,
    IReadOnlyList<AiMLapData> Laps,
    AiMSessionStream? Stream = null);

/// <summary>
/// The whole logged GPS stream and channels of a GPS logger session, on the same clock as
/// <see cref="AiMLapData.StartTimeMs"/>, so laps can be re-cut at a different timing line. Released after load.
/// </summary>
internal sealed record AiMSessionStream(
    IReadOnlyList<TelemetryPoint> Points,
    IReadOnlyList<AiMChannelData> Channels);

internal sealed record AiMLapData(
    int Number,
    double StartTimeMs,
    double DurationMs,
    IReadOnlyList<TelemetryPoint> Points,
    IReadOnlyList<AiMChannelData> Channels);

internal sealed record AiMChannelData(
    string Name,
    string Units,
    IReadOnlyList<AiMChannelSample> Samples);

// A struct: sessions hold millions of these, and per-sample heap objects made loading and lap cutting slow.
internal readonly record struct AiMChannelSample(double TimeMs, double Value);

internal sealed class ChartChannelSetting(string key, string name, string units, bool isEnabled)
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public string Units { get; } = units;
    public bool IsEnabled { get; set; } = isEnabled;

    // Which kinds of data provide this channel, e.g. "Catalyst, AiM"; empty when unknown.
    public string Sources { get; set; } = string.Empty;

    public string PickerLabel => string.IsNullOrEmpty(Sources) ? Name : $"{Name}  ({Sources})";

    public bool MatchesFilter(string filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        filter.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(term =>
            Name.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
            Sources.Contains(term, StringComparison.CurrentCultureIgnoreCase));
}

/// <summary>Labels chart channels with the kinds of session data they come from (Catalyst, AiM, VBox).</summary>
internal static class ChannelSources
{
    public const string LoggerKeyPrefix = "aim:";
    private static readonly string[] Order = ["Catalyst", "AiM", "VBox"];

    public static string Of(SessionInfo session) =>
        Path.GetExtension(session.FilePath).Equals(".vbo", StringComparison.OrdinalIgnoreCase) ? "VBox"
        : session.AiMData is not null ? "AiM"
        : "Catalyst";

    /// <summary>
    /// Source labels by channel key: logger channels ("aim:Name") get the sources whose files contain them; every
    /// other key (speed, G, delta) is available from every loaded source and uses <c>""</c> as its entry.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Discover(IEnumerable<SessionInfo> sessions)
    {
        var all = new HashSet<string>();
        var byChannel = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in sessions)
        {
            var source = Of(session);
            all.Add(source);
            if (session.AiMData is null)
            {
                continue;
            }

            foreach (var name in session.AiMData.Laps.SelectMany(lap => lap.Channels).Select(channel => channel.Name)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var key = LoggerKeyPrefix + name;
                if (!byChannel.TryGetValue(key, out var set))
                {
                    byChannel[key] = set = [];
                }

                set.Add(source);
            }
        }

        var result = byChannel.ToDictionary(item => item.Key, item => Join(item.Value), StringComparer.OrdinalIgnoreCase);
        result[string.Empty] = Join(all);
        return result;
    }

    public static void Apply(IEnumerable<ChartChannelSetting> channels, IReadOnlyDictionary<string, string> sources)
    {
        foreach (var channel in channels)
        {
            channel.Sources = sources.TryGetValue(channel.Key, out var label)
                ? label
                : channel.Key.StartsWith(LoggerKeyPrefix, StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : sources.GetValueOrDefault(string.Empty, string.Empty);
        }
    }

    private static string Join(IReadOnlySet<string> sources) => string.Join(", ", Order.Where(sources.Contains));
}

internal sealed record SessionEdits(
    string VehicleName,
    string TrackCondition,
    string RaceType,
    string DriverName = "");

internal sealed class ViewerSettings
{
    public string DistanceUnits { get; set; } = "Metric";
    public List<string> CustomVehicleNames { get; set; } = [];
    public List<string> HiddenVehicleNames { get; set; } = [];
    public Dictionary<string, string> VehicleMappings { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> SessionVehicleMappings { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> ChannelVisibility { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<string> ChannelOrder { get; set; } = [];
    public Dictionary<string, string> SessionLayoutAssignments { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> SessionTrackAssignments { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> TrackMappings { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    // Driver names by DriverSources key (Garmin profile, logger driver field, or folder of a file with no driver).
    public Dictionary<string, string> DriverMappings { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    // Chart heights (device-independent pixels) by chart channel key, set by dragging a chart's bottom edge.
    public Dictionary<string, double> ChartHeights { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class VehicleAliasSetting(string alias, string vehicleName)
{
    public string Alias { get; } = alias;
    public string VehicleName { get; set; } = vehicleName;
}

internal sealed class DriverAliasSetting(string key, string detectedName, string source, string sourceDetail, int sessionCount, string name)
{
    public string Key { get; } = key;
    public string DetectedName { get; } = detectedName;
    public string Source { get; } = source;
    public string SourceDetail { get; } = sourceDetail;
    public int SessionCount { get; } = sessionCount;
    public string Name { get; set; } = name;
}

/// <summary>
/// Works out where a session's driver name comes from so it can be renamed in settings. Garmin sessions are keyed by
/// driver profile, logger files by the driver name they carry, and files with no driver by their folder, so a folder
/// per driver (e.g. "PitWall Data\Mat") can be assigned in one go without touching other unnamed files.
/// </summary>
internal static class DriverSources
{
    public const string UnknownDriver = "Unknown driver";

    public static (string Key, string DetectedName, string Source, string SourceDetail) Identify(
        SessionInfo session, IReadOnlyDictionary<string, string> profileNames)
    {
        if (!string.IsNullOrWhiteSpace(session.GarminGuid))
        {
            return profileNames.TryGetValue(session.GarminGuid, out var profileName) &&
                   !string.IsNullOrWhiteSpace(profileName)
                ? ($"garmin:{session.GarminGuid}", profileName, "Garmin driver profile", session.GarminGuid)
                : ($"garmin:{session.GarminGuid}", UnknownDriver, "Garmin profile with no name", session.GarminGuid);
        }

        if (!string.IsNullOrWhiteSpace(session.AiMData?.DriverName))
        {
            var name = session.AiMData.DriverName.Trim();
            return ($"logger:{name}", name, "Driver named in logger file", name);
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(session.FilePath)) ?? string.Empty;
        var folderName = Path.GetFileName(folder);
        return ($"folder:{folder}", UnknownDriver,
            string.IsNullOrWhiteSpace(folderName) ? "No driver in file" : $"No driver in file · {folderName} folder",
            folder);
    }

    public static string Resolve(
        SessionInfo session, IReadOnlyDictionary<string, string> profileNames, IReadOnlyDictionary<string, string> mappings)
    {
        var (key, detectedName, _, _) = Identify(session, profileNames);
        return mappings.TryGetValue(key, out var mapped) && !string.IsNullOrWhiteSpace(mapped)
            ? mapped.Trim()
            : detectedName;
    }

    // One row per driver source in the loaded sessions, plus saved mappings whose sessions aren't loaded right now.
    public static IReadOnlyList<DriverAliasSetting> Build(
        IEnumerable<SessionInfo> sessions, IReadOnlyDictionary<string, string> profileNames,
        IReadOnlyDictionary<string, string> mappings)
    {
        var rows = sessions
            .Select(session => Identify(session, profileNames))
            .GroupBy(source => source.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var source = group.First();
                return new DriverAliasSetting(
                    source.Key, source.DetectedName, source.Source, source.SourceDetail, group.Count(),
                    mappings.TryGetValue(source.Key, out var mapped) ? mapped : string.Empty);
            })
            .ToList();
        var present = new HashSet<string>(rows.Select(row => row.Key), StringComparer.OrdinalIgnoreCase);
        rows.AddRange(mappings
            .Where(pair => !present.Contains(pair.Key))
            .Select(pair => new DriverAliasSetting(pair.Key, pair.Key[(pair.Key.IndexOf(':') + 1)..],
                "Saved mapping (no sessions loaded)", pair.Key, 0, pair.Value)));
        return rows
            .OrderBy(row => row.DetectedName.Equals(UnknownDriver, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(row => row.DetectedName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.Source, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}

internal sealed class TrackAliasSetting(string key, string alias, string defaultName, string name)
{
    public string Key { get; } = key;
    public string Alias { get; } = alias;
    public string DefaultName { get; } = defaultName;
    public string Name { get; set; } = name;
}

internal sealed class VehicleVisibilitySetting(string name, bool isHidden) : INotifyPropertyChanged
{
    public string OriginalName { get; } = name;
    public string Name { get; set; } = name;
    private bool _isHidden = isHidden;

    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (_isHidden == value)
            {
                return;
            }

            _isHidden = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VisibilityGlyph));
            OnPropertyChanged(nameof(VisibilityToolTip));
        }
    }

    public string VisibilityGlyph => IsHidden ? "○" : "◉";
    public string VisibilityToolTip => IsHidden ? "Show vehicle" : "Hide vehicle";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed record VehicleCatalogEntry(string Name, string GarminGuid, IReadOnlyList<string> VehicleIds);

internal sealed record VehicleCatalog(IReadOnlyList<VehicleCatalogEntry> Vehicles, IReadOnlyList<string> Names)
{
    public string? ResolveAlias(string alias)
    {
        if (alias.Equals("3f9dda44-c22e-4dec-a392-b46b0b3537b8", StringComparison.OrdinalIgnoreCase))
        {
            return "2023 PORSCHE GT4 (variant unknown)";
        }

        var aliasMatches = Vehicles.Where(vehicle =>
                vehicle.VehicleIds.Contains(alias, StringComparer.OrdinalIgnoreCase))
            .Select(vehicle => vehicle.Name)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (aliasMatches.Length == 1)
        {
            return aliasMatches[0];
        }

        var accountGuidMatches = Vehicles.Where(vehicle =>
                vehicle.GarminGuid.Equals(alias, StringComparison.OrdinalIgnoreCase))
            .Select(vehicle => vehicle.Name)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return accountGuidMatches.Length == 1 ? accountGuidMatches[0] : null;
    }
}

internal sealed record TrackPoint(double DistanceM, double Latitude, double Longitude, double WidthM, double AltitudeM = double.NaN);

/// <summary>
/// Track segment layout from a meanline file. <paramref name="BoundariesM"/> holds the distance where
/// each segment hands over to the next (one fewer entry than segments), measured over <paramref name="LengthM"/>.
/// </summary>
internal sealed record TrackSegmentMap(IReadOnlyList<double> BoundariesM, double LengthM)
{
    public int IndexAt(double distanceM)
    {
        var index = 0;
        while (index < BoundariesM.Count && distanceM >= BoundariesM[index])
        {
            index++;
        }

        return index;
    }

    public TrackSegmentMap ScaledTo(double lengthM)
    {
        var scale = LengthM > 0 ? lengthM / LengthM : 1;
        return new TrackSegmentMap(BoundariesM.Select(boundary => boundary * scale).ToArray(), lengthM);
    }
}

internal sealed record TrackCarGroup(string Name, IReadOnlyList<SessionInfo> Sessions);

internal sealed record TrackDriverGroup(
    string Name,
    IReadOnlyList<SessionInfo> Sessions,
    LapInfo? OptimalLap = null);

internal sealed record TrackDateGroup(DateTime Date, IReadOnlyList<SessionInfo> Sessions);

internal sealed record SessionLayoutChoice(string Label, string Value);

internal sealed record TrackDriverStats(
    string DriverName,
    string Laps,
    IReadOnlyList<TrackCarStats> Cars);

internal sealed record TrackCarStats(
    string VehicleName,
    int Sessions,
    LapNavigationTarget? FastestLap,
    LapNavigationTarget? OptimalLap,
    int Laps = 0)
{
    public string FastestLapLabel => FastestLap?.Label ?? "Fastest —";
    public string OptimalLapLabel => OptimalLap?.Label ?? "Optimal —";
    public string? FastestLapToolTip => FastestLap?.ToolTip;
    public string? OptimalLapToolTip => OptimalLap?.ToolTip;
    public bool HasFastestLap => FastestLap is not null;
    public bool HasOptimalLap => OptimalLap is not null;
}

internal sealed record LapNavigationTarget(SessionInfo Session, double DurationMs, bool IsOptimal)
{
    public string Label => $"{(IsOptimal ? "Optimal" : "Fastest")} · {FormatLapTime(DurationMs / 1000d)}";
    public string ToolTip =>
        $"{(IsOptimal ? "Optimal Lap" : "Fastest Lap")}: {Session.LocalStart:MMMM d, yyyy h:mm tt}";

    private static string FormatLapTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}";
    }
}

internal sealed record TrackLocation(
    string Key,
    double Latitude,
    double Longitude,
    IReadOnlyList<SessionInfo> Sessions,
    string TrackName,
    string ConfigurationName,
    double LengthM,
    string? MeanlinePath,
    string? DisplayNameOverride = null)
{
    public string DisplayName => !string.IsNullOrWhiteSpace(DisplayNameOverride)
        ? DisplayNameOverride
        : string.IsNullOrWhiteSpace(ConfigurationName)
            ? TrackName
            : $"{TrackName} · {ConfigurationName}";
}

internal sealed record TrackLayoutAssignment(TrackLocation Location, string LayoutName);

internal sealed record LapInfo(
    SessionInfo Session,
    int Index,
    bool IsAverage,
    string Label,
    double DurationMs,
    double StartTimeSessionMs,
    bool IsOptimal = false,
    string? DataFilePath = null)
{
    public string TreeLabel => IsOptimal
        ? $"{Label}  ·  {FormatLapTime(DurationMs / 1000d)}  ·  {Session.LocalStart.AddMilliseconds(StartTimeSessionMs):MMM d, yyyy  h:mm tt}"
        : $"{Label}  ·  {FormatLapTime(DurationMs / 1000d)}";
    public string ReferenceLabel =>
        $"{Session.LocalStart.AddMilliseconds(StartTimeSessionMs):MMM d, yyyy  h:mm tt}  ·  {Label}  ·  {FormatLapTime(DurationMs / 1000d)}";

    private static string FormatLapTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}";
    }
}

internal readonly record struct TelemetryPoint(
    double DistanceM,
    double TimeMs,
    double SpeedMps,
    double LongitudinalG,
    double LateralG,
    double Latitude,
    double Longitude);

internal sealed record SessionTrace(LapInfo Lap, IReadOnlyList<TelemetryPoint> Points)
{
    public SessionInfo Session => Lap.Session;
    public double LapDurationSeconds => Lap.DurationMs / 1000d;
    public double DistanceM => Points.Count == 0 ? 0 : Points[^1].DistanceM;

    public IReadOnlyList<ChartValue> GetChannelValues(string channelName)
    {
        if (Lap.Session.AiMData is not { } aimData ||
            Lap.Index < 0 || Lap.Index >= aimData.Laps.Count)
        {
            return [];
        }

        var channel = aimData.Laps[Lap.Index].Channels.FirstOrDefault(candidate =>
            candidate.Name.Equals(channelName, StringComparison.OrdinalIgnoreCase));
        if (channel is null)
        {
            return [];
        }

        return channel.Samples.Select(sample => new ChartValue(
                InterpolateDistanceM(Points, sample.TimeMs),
                sample.Value))
            .ToArray();
    }

    private static double InterpolateDistanceM(IReadOnlyList<TelemetryPoint> points, double timeMs)
    {
        var low = 0;
        var high = points.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (points[middle].TimeMs < timeMs)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (low == 0)
        {
            return points[0].DistanceM;
        }

        if (low >= points.Count)
        {
            return points[^1].DistanceM;
        }

        var before = points[low - 1];
        var after = points[low];
        var span = after.TimeMs - before.TimeMs;
        return span <= 0
            ? after.DistanceM
            : before.DistanceM + (after.DistanceM - before.DistanceM) *
                ((timeMs - before.TimeMs) / span);
    }
}

internal sealed record ChartValue(double DistanceM, double Value);

#if !PITWALL_AVALONIA
internal sealed record ChartLine(
    string Label,
    IReadOnlyList<ChartValue> Values,
    System.Windows.Media.Brush Color,
    bool Dashed = false,
    string DeltaKey = "");
#endif
