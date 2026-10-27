using System.Text.Json;
using TrackTelemetryViewer;

namespace PitWallApp;

// Reads and writes the same files as the WPF app. Settings are merged from the per-user copy and the data
// folder on load and always saved next to the data; session overrides live in the per-user folder.
internal static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly string UserFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackTelemetryViewer");

    public static string SessionEditsPath => Path.Combine(UserFolder, "session-overrides.json");

    public static string ViewerSettingsPath(string dataFolder) => Path.Combine(dataFolder, "viewer-settings.json");

    public static ViewerSettings Clone(ViewerSettings source) => new()
    {
        DistanceUnits = source.DistanceUnits,
        CustomVehicleNames = [.. source.CustomVehicleNames],
        HiddenVehicleNames = [.. source.HiddenVehicleNames],
        VehicleMappings = new Dictionary<string, string>(source.VehicleMappings, StringComparer.OrdinalIgnoreCase),
        SessionVehicleMappings = new Dictionary<string, string>(
            source.SessionVehicleMappings, StringComparer.OrdinalIgnoreCase),
        ChannelVisibility = new Dictionary<string, bool>(source.ChannelVisibility, StringComparer.OrdinalIgnoreCase),
        ChannelOrder = [.. source.ChannelOrder],
        SessionLayoutAssignments = new Dictionary<string, string>(
            source.SessionLayoutAssignments, StringComparer.OrdinalIgnoreCase),
        TrackMappings = new Dictionary<string, string>(source.TrackMappings, StringComparer.OrdinalIgnoreCase)
    };

    public static ViewerSettings Load(string dataFolder, List<string> warnings)
    {
        var settings = new ViewerSettings();
        var paths = new[] { Path.Combine(UserFolder, "viewer-settings.json"), ViewerSettingsPath(dataFolder) }
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var loaded = JsonSerializer.Deserialize<ViewerSettings>(File.ReadAllText(path))
                             ?? throw new InvalidDataException("The settings file did not contain a JSON object.");
                settings.DistanceUnits =
                    loaded.DistanceUnits?.Equals("Imperial", StringComparison.OrdinalIgnoreCase) == true
                        ? "Imperial"
                        : "Metric";
                settings.CustomVehicleNames = settings.CustomVehicleNames.Concat(loaded.CustomVehicleNames ?? [])
                    .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
                settings.HiddenVehicleNames = settings.HiddenVehicleNames.Concat(loaded.HiddenVehicleNames ?? [])
                    .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
                Merge(settings.TrackMappings, loaded.TrackMappings);
                Merge(settings.SessionLayoutAssignments, loaded.SessionLayoutAssignments);
                Merge(settings.SessionVehicleMappings, loaded.SessionVehicleMappings);
                Merge(settings.VehicleMappings, loaded.VehicleMappings);
                foreach (var (key, visible) in loaded.ChannelVisibility ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        settings.ChannelVisibility[key] = visible;
                    }
                }

                if (loaded.ChannelOrder is { Count: > 0 })
                {
                    settings.ChannelOrder = loaded.ChannelOrder.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               JsonException or InvalidDataException)
            {
                warnings.Add($"Settings could not be loaded from {path}: {exception.Message}");
            }
        }

        var legacyPath = Path.Combine(dataFolder, "vehicle-name-mappings.json");
        if (File.Exists(legacyPath))
        {
            try
            {
                var legacy = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(legacyPath));
                foreach (var (alias, name) in legacy ?? [])
                {
                    settings.VehicleMappings.TryAdd(alias, name);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                warnings.Add($"Legacy vehicle mappings could not be loaded from {legacyPath}: {exception.Message}");
            }
        }

        return settings;
    }

    private static void Merge(Dictionary<string, string> target, Dictionary<string, string>? source)
    {
        foreach (var (key, value) in source ?? [])
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                target[key] = value;
            }
        }
    }

    public static string? Save(string dataFolder, ViewerSettings settings)
    {
        var path = ViewerSettingsPath(dataFolder);
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return $"Could not save {path}: {exception.Message}";
        }
    }

    public static Dictionary<string, SessionEdits> LoadEdits(List<string> warnings)
    {
        var edits = new Dictionary<string, SessionEdits>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(SessionEditsPath))
        {
            return edits;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, SessionEdits>>(
                File.ReadAllText(SessionEditsPath));
            foreach (var (key, value) in saved ?? [])
            {
                if (!string.IsNullOrWhiteSpace(value.VehicleName))
                {
                    edits[key] = value;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            warnings.Add($"Saved session corrections could not be loaded: {exception.Message}");
        }

        return edits;
    }

    public static string? SaveEdits(Dictionary<string, SessionEdits> edits)
    {
        try
        {
            Directory.CreateDirectory(UserFolder);
            File.WriteAllText(SessionEditsPath, JsonSerializer.Serialize(edits, JsonOptions));
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return $"Session corrections could not be saved: {exception.Message}";
        }
    }
}
