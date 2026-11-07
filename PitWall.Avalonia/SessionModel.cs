using System.Globalization;
using System.Text.Json;
using TrackTelemetryViewer;

namespace PitWallApp;

internal sealed record CsvVenueAssignment(
    string TrackName,
    string Configuration,
    double Latitude,
    double Longitude,
    AimCsvTransform? Transform,
    int Version = 4);

// Venue grouping and name resolution ported from the WPF MainWindow so both apps read the same settings files.
internal sealed class SessionModel
{
    public const double TrackMergeDistanceM = 5 * 1609.344;
    public const string UnspecifiedLayout = "__unspecified_layout__";
    public const string UseSourceLayout = "__use_source_layout__";
    public const string LayoutUnspecifiedLabel = "Layout unspecified";

    private const string CsvVenueKeyPrefix = "venue:";
    private const int CsvAssignmentVersion = 4;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Dictionary<string, TrackLocation> _trackBySession = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TrackLocation> _locations = [];
    private readonly List<SessionInfo> _sessions;

    public SessionModel(
        IEnumerable<SessionInfo> sessions,
        ViewerSettings settings,
        VehicleCatalog catalog,
        IReadOnlyDictionary<string, string> driverNames,
        Dictionary<string, SessionEdits> edits)
    {
        _sessions = sessions.ToList();
        Settings = settings;
        Catalog = catalog;
        DriverNames = driverNames;
        Edits = edits;
        BuildTrackLocations();
    }

    public IReadOnlyList<SessionInfo> Sessions => _sessions;
    public ViewerSettings Settings { get; set; }
    public VehicleCatalog Catalog { get; }
    public IReadOnlyDictionary<string, string> DriverNames { get; }
    public Dictionary<string, SessionEdits> Edits { get; }
    public IReadOnlyList<TrackLocation> Locations => _locations;

    // CSV sessions as read from disk, before a track assignment overlays them onto another venue.
    public Dictionary<string, SessionInfo> CsvOriginals { get; } = new(StringComparer.OrdinalIgnoreCase);

    public TrackLocation? LocationOf(SessionInfo session) =>
        _trackBySession.TryGetValue(session.FilePath, out var location) ? location : null;

    public string? MeanlinePathOf(SessionInfo session) => session.MeanlinePath ?? LocationOf(session)?.MeanlinePath;

    public void ReplaceSession(SessionInfo session)
    {
        var index = _sessions.FindIndex(item => item.FilePath.Equals(session.FilePath, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _sessions[index] = session;
        }
    }

    public static bool IsCsvSession(SessionInfo session) =>
        session.AiMData is not null && session.FilePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

    public static string CsvVenuePrefixedKey(string venue) => CsvVenueKeyPrefix + venue.Trim();

    public static CsvVenueAssignment? GetCsvAssignment(ViewerSettings settings, string venue)
    {
        if (!settings.TrackMappings.TryGetValue(CsvVenuePrefixedKey(venue), out var json))
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

    public static string SerializeCsvAssignment(CsvVenueAssignment assignment) =>
        JsonSerializer.Serialize(assignment with { Version = CsvAssignmentVersion });

    public static double GetCsvShiftSeconds(ViewerSettings settings, string venue) =>
        GetCsvAssignment(settings, venue)?.Transform?.ShiftSeconds ?? 0;

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

            if (!CsvOriginals.TryGetValue(current.FilePath, out var original))
            {
                original = current;
                CsvOriginals[current.FilePath] = original;
            }

            var assignment = GetCsvAssignment(Settings, original.AiMData!.TrackName);
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

    public void BuildTrackLocations()
    {
        _locations.Clear();
        _trackBySession.Clear();
        ApplyCsvTrackAssignments();
        ApplySessionTrackAssignments();

        var sessions = _sessions.OrderBy(session => session.Latitude)
            .ThenBy(session => session.Longitude)
            .ThenBy(session => session.LocalStart)
            .ToArray();
        var parents = Enumerable.Range(0, sessions.Length).ToArray();

        int Find(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }

            return index;
        }

        for (var left = 0; left < sessions.Length; left++)
        {
            for (var right = left + 1; right < sessions.Length; right++)
            {
                if (DistanceMeters(sessions[left].Latitude, sessions[left].Longitude,
                        sessions[right].Latitude, sessions[right].Longitude) <= TrackMergeDistanceM)
                {
                    var leftRoot = Find(left);
                    var rightRoot = Find(right);
                    if (leftRoot != rightRoot)
                    {
                        parents[rightRoot] = leftRoot;
                    }
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
                .FirstOrDefault() ?? string.Create(CultureInfo.InvariantCulture,
                $"Track {clusterLatitude:F2}, {clusterLongitude:F2}");

            var distinctLayouts = nearbySessions
                .Select(GetEffectiveLayoutName)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var displayGroups = nearbySessions.GroupBy(session =>
            {
                var effectiveConfiguration = GetEffectiveLayoutName(session);
                var configurationLabel = string.IsNullOrWhiteSpace(effectiveConfiguration)
                    ? distinctLayouts.Length > 1 ? LayoutUnspecifiedLabel : string.Empty
                    : effectiveConfiguration;
                var aliasKey = CreateTrackAliasKey(
                    string.IsNullOrWhiteSpace(session.MeanlineTrackName) ? "Unknown track" : trackName,
                    effectiveConfiguration);
                return Settings.TrackMappings.TryGetValue(aliasKey, out var mappedName) &&
                       !string.IsNullOrWhiteSpace(mappedName)
                    ? mappedName.Trim()
                    : string.IsNullOrWhiteSpace(configurationLabel)
                        ? trackName
                        : $"{trackName} · {configurationLabel}";
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
                        ? LayoutUnspecifiedLabel
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
                if (meanlinePath is null && lengthM > 0)
                {
                    // Logger-only groups (AiM/VBOX) have no meanline of their own; borrow the closest-length one from the same venue.
                    var borrowed = nearbySessions
                        .Where(session => session.MeanlinePath is not null && session.MeanlineLengthM > 0)
                        .OrderBy(session => Math.Abs(session.MeanlineLengthM - lengthM))
                        .FirstOrDefault();
                    if (borrowed is not null && Math.Abs(borrowed.MeanlineLengthM - lengthM) < lengthM * 0.1)
                    {
                        meanlinePath = borrowed.MeanlinePath;
                    }
                }

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

    public static string CreateTrackAliasKey(string trackName, string configurationName) =>
        JsonSerializer.Serialize(new[] { trackName.Trim(), configurationName.Trim() });

    public string GetEffectiveLayoutName(SessionInfo session)
    {
        if (Settings.SessionTrackAssignments.TryGetValue(session.SessionGuid, out var targetGuid) &&
            !targetGuid.Equals(session.SessionGuid, StringComparison.OrdinalIgnoreCase))
        {
            var target = _sessions.FirstOrDefault(candidate =>
                candidate.SessionGuid.Equals(targetGuid, StringComparison.OrdinalIgnoreCase));
            if (target is not null)
            {
                return GetSourceLayoutName(target);
            }
        }

        return GetSourceLayoutName(session);
    }

    private string GetSourceLayoutName(SessionInfo session)
    {
        if (Settings.SessionLayoutAssignments.TryGetValue(session.SessionGuid, out var assignedLayout))
        {
            return assignedLayout.Equals(UnspecifiedLayout, StringComparison.OrdinalIgnoreCase) ||
                   assignedLayout.Equals(LayoutUnspecifiedLabel, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : assignedLayout.Trim();
        }

        return session.MeanlineConfigurationName.Equals(LayoutUnspecifiedLabel, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : session.MeanlineConfigurationName.Trim();
    }

    private void ApplySessionTrackAssignments()
    {
        if (Settings.SessionTrackAssignments.Count == 0)
        {
            return;
        }

        var targets = _sessions.GroupBy(session => session.SessionGuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < _sessions.Count; index++)
        {
            var source = _sessions[index];
            if (!Settings.SessionTrackAssignments.TryGetValue(source.SessionGuid, out var targetGuid) ||
                targetGuid.Equals(source.SessionGuid, StringComparison.OrdinalIgnoreCase) ||
                !targets.TryGetValue(targetGuid, out var target))
            {
                continue;
            }

            _sessions[index] = source with
            {
                Latitude = target.Latitude,
                Longitude = target.Longitude,
                MeanlinePath = target.MeanlinePath,
                MeanlineTrackName = target.MeanlineTrackName,
                MeanlineConfigurationName = GetSourceLayoutName(target),
                MeanlineLengthM = target.MeanlineLengthM
            };
        }
    }

    // Same precedence as WPF: a per-session assignment wins over the vehicle-alias mapping.
    public string GetVehicleName(SessionInfo session)
    {
        if (Settings.SessionVehicleMappings.TryGetValue(session.VehicleAssignmentKey, out var mappedName))
        {
            return mappedName;
        }

        if (Settings.SessionVehicleMappings.TryGetValue(session.SessionGuid, out mappedName))
        {
            return mappedName;
        }

        if (!string.IsNullOrWhiteSpace(session.VehicleAlias) &&
            Settings.VehicleMappings.TryGetValue(session.VehicleAlias, out mappedName))
        {
            return mappedName;
        }

        if (Edits.TryGetValue(session.SessionGuid, out var edits) && !string.IsNullOrWhiteSpace(edits.VehicleName))
        {
            return edits.VehicleName;
        }

        return !string.IsNullOrWhiteSpace(session.AiMData?.VehicleName)
            ? session.AiMData.VehicleName
            : Catalog.ResolveAlias(session.VehicleAlias) ?? session.DefaultVehicleName;
    }

    public bool IsVehicleHidden(string vehicleName) =>
        Settings.HiddenVehicleNames.Contains(vehicleName, StringComparer.CurrentCultureIgnoreCase);

    public string GetProfileDriverName(SessionInfo session) =>
        DriverNames.TryGetValue(session.GarminGuid, out var driverName)
            ? driverName
            : string.IsNullOrWhiteSpace(session.AiMData?.DriverName)
                ? "Unknown driver"
                : session.AiMData.DriverName;

    public SessionEdits GetSessionEdits(SessionInfo session) =>
        Edits.TryGetValue(session.SessionGuid, out var edits)
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

    public string GetDriverName(SessionInfo session) => GetSessionEdits(session).DriverName;

    public bool IsWet(SessionInfo session) =>
        GetSessionEdits(session).TrackCondition.Contains("WET", StringComparison.OrdinalIgnoreCase);

    // Layouts that an unspecified-layout track at this venue can be assigned to.
    public IReadOnlyList<string> GetAssignableLayouts(TrackLocation location) =>
        !location.ConfigurationName.Equals(LayoutUnspecifiedLabel, StringComparison.OrdinalIgnoreCase)
            ? []
            : _locations
                .Where(candidate =>
                    !ReferenceEquals(candidate, location) &&
                    candidate.TrackName.Equals(location.TrackName, StringComparison.CurrentCultureIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(candidate.ConfigurationName) &&
                    !candidate.ConfigurationName.Equals(LayoutUnspecifiedLabel, StringComparison.OrdinalIgnoreCase) &&
                    DistanceMeters(candidate.Latitude, candidate.Longitude, location.Latitude, location.Longitude) <=
                    TrackMergeDistanceM)
                .Select(candidate => candidate.ConfigurationName)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

    public IReadOnlyList<SessionLayoutChoice> BuildSessionLayoutChoices(SessionInfo session)
    {
        var trackName = LocationOf(session)?.TrackName ?? session.MeanlineTrackName;
        var layouts = Sessions
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

        var sourceUnspecified = string.IsNullOrWhiteSpace(session.MeanlineConfigurationName) ||
                                session.MeanlineConfigurationName.Equals(
                                    LayoutUnspecifiedLabel, StringComparison.OrdinalIgnoreCase);
        var choices = new List<SessionLayoutChoice>
        {
            new(sourceUnspecified
                    ? "Use data layout (unspecified)"
                    : $"Use data layout ({session.MeanlineConfigurationName.Trim()})",
                UseSourceLayout)
        };
        choices.AddRange(layouts.Select(name => new SessionLayoutChoice(
            string.IsNullOrWhiteSpace(trackName) ? name : $"{trackName} · {name}", name)));
        if (layouts.Length > 1 || sourceUnspecified ||
            Settings.SessionLayoutAssignments.TryGetValue(session.SessionGuid, out var assignment) &&
            assignment.Equals(UnspecifiedLayout, StringComparison.OrdinalIgnoreCase))
        {
            choices.Add(new SessionLayoutChoice(
                string.IsNullOrWhiteSpace(trackName)
                    ? LayoutUnspecifiedLabel
                    : $"{trackName} · {LayoutUnspecifiedLabel}",
                UnspecifiedLayout));
        }

        return choices;
    }

    public IReadOnlyList<TrackAliasSetting> BuildTrackAliasSettings() => Sessions
        .Select(session =>
        {
            var configurationName = GetEffectiveLayoutName(session);
            var sourceTrackName = string.IsNullOrWhiteSpace(session.MeanlineTrackName)
                ? "Unknown track"
                : session.MeanlineTrackName.Trim();
            var trackName = LocationOf(session)?.TrackName ?? sourceTrackName;
            var key = CreateTrackAliasKey(sourceTrackName, configurationName);
            var isUnspecifiedWithAlternatives = string.IsNullOrWhiteSpace(configurationName) &&
                _locations.Any(candidate =>
                    !string.IsNullOrWhiteSpace(candidate.ConfigurationName) &&
                    !candidate.ConfigurationName.Equals(LayoutUnspecifiedLabel, StringComparison.OrdinalIgnoreCase) &&
                    candidate.TrackName.Equals(trackName, StringComparison.CurrentCultureIgnoreCase) &&
                    DistanceMeters(candidate.Latitude, candidate.Longitude, session.Latitude, session.Longitude) <=
                    TrackMergeDistanceM);
            var defaultName = string.IsNullOrWhiteSpace(configurationName)
                ? isUnspecifiedWithAlternatives ? $"{trackName} · {LayoutUnspecifiedLabel}" : trackName
                : $"{trackName} · {configurationName}";
            var layoutLabel = string.IsNullOrWhiteSpace(configurationName)
                ? $"{trackName} · {LayoutUnspecifiedLabel}"
                : defaultName;
            var displayName = Settings.TrackMappings.TryGetValue(key, out var mappedName) ? mappedName : defaultName;
            return new TrackAliasSetting(key, layoutLabel, defaultName, displayName);
        })
        .GroupBy(alias => alias.Key, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToArray();

    public static double DistanceMeters(double latitudeA, double longitudeA, double latitudeB, double longitudeB)
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
}
