using System.ComponentModel;
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
    IReadOnlyList<AiMLapData> Laps);

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

internal sealed record AiMChannelSample(double TimeMs, double Value);

internal sealed class ChartChannelSetting(string key, string name, string units, bool isEnabled)
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public string Units { get; } = units;
    public bool IsEnabled { get; set; } = isEnabled;
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
    public Dictionary<string, string> SessionLayoutAssignments { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> TrackMappings { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class VehicleAliasSetting(string alias, string vehicleName)
{
    public string Alias { get; } = alias;
    public string VehicleName { get; set; } = vehicleName;
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
    public string Name { get; } = name;
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

internal sealed record TrackPoint(double DistanceM, double Latitude, double Longitude, double WidthM);

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
    LapNavigationTarget? OptimalLap)
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

internal sealed record ChartLine(
    string Label,
    IReadOnlyList<ChartValue> Values,
    System.Windows.Media.Brush Color,
    bool Dashed = false);
