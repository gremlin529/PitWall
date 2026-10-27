using System.Globalization;
using System.Text.Json;
using TrackTelemetryViewer;

namespace PitWallApp;

// Venue grouping and name resolution ported from the WPF MainWindow so both apps read the same settings files.
internal sealed class SessionModel
{
    public const double TrackMergeDistanceM = 5 * 1609.344;
    public const string UnspecifiedLayout = "__unspecified_layout__";
    public const string UseSourceLayout = "__use_source_layout__";
    public const string LayoutUnspecifiedLabel = "Layout unspecified";

    private readonly Dictionary<string, TrackLocation> _trackBySession = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TrackLocation> _locations = [];

    public SessionModel(
        IReadOnlyList<SessionInfo> sessions,
        ViewerSettings settings,
        VehicleCatalog catalog,
        IReadOnlyDictionary<string, string> driverNames,
        Dictionary<string, SessionEdits> edits)
    {
        Sessions = sessions;
        Settings = settings;
        Catalog = catalog;
        DriverNames = driverNames;
        Edits = edits;
        BuildTrackLocations();
    }

    public IReadOnlyList<SessionInfo> Sessions { get; }
    public ViewerSettings Settings { get; set; }
    public VehicleCatalog Catalog { get; }
    public IReadOnlyDictionary<string, string> DriverNames { get; }
    public Dictionary<string, SessionEdits> Edits { get; }
    public IReadOnlyList<TrackLocation> Locations => _locations;

    public TrackLocation? LocationOf(SessionInfo session) =>
        _trackBySession.TryGetValue(session.FilePath, out var location) ? location : null;

    public void BuildTrackLocations()
    {
        _locations.Clear();
        _trackBySession.Clear();

        var sessions = Sessions.OrderBy(session => session.Latitude)
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

    public string GetVehicleName(SessionInfo session)
    {
        if (!string.IsNullOrWhiteSpace(session.VehicleAlias) &&
            Settings.VehicleMappings.TryGetValue(session.VehicleAlias, out var mappedName))
        {
            return mappedName;
        }

        if (Settings.SessionVehicleMappings.TryGetValue(session.SessionGuid, out mappedName))
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
