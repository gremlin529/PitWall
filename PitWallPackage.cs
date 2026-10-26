using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace TrackTelemetryViewer;

internal sealed class PackageSession
{
    public string SessionGuid { get; set; } = "";
    public string Kind { get; set; } = "garmin";
    public string Driver { get; set; } = "";
    public string Vehicle { get; set; } = "";
    public string TrackName { get; set; } = "";
    public string Layout { get; set; } = "";
    public string TrackCondition { get; set; } = "";
    public string RaceType { get; set; } = "";
    public DateTime LocalStart { get; set; }
    public List<string> Files { get; set; } = [];
    public string PrimaryFile { get; set; } = "";
}

internal sealed class PackageManifest
{
    public int FormatVersion { get; set; } = 1;
    public string App { get; set; } = "PitWall";
    public DateTime ExportedUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string> TrackMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PackageSession> Sessions { get; set; } = [];
}

internal sealed record ExportSessionSource(
    SessionInfo Session,
    string Driver,
    string Vehicle,
    string TrackName,
    string Layout,
    string TrackCondition,
    string RaceType);

internal sealed record ImportedSession(PackageSession Entry, string PrimaryPath);

internal static class PitWallPackage
{
    public const string Extension = ".pitwall";
    private const string ManifestName = "manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static void Export(
        IReadOnlyList<ExportSessionSource> sources,
        IReadOnlyDictionary<string, string> trackMappings,
        string destinationPath)
    {
        var manifest = new PackageManifest();
        foreach (var (key, value) in trackMappings)
        {
            manifest.TrackMappings[key] = value;
        }

        var temporaryPath = destinationPath + ".tmp";
        using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
        {
            foreach (var source in sources)
            {
                var session = source.Session;
                var isXrk = session.AiMData is not null;
                var files = isXrk
                    ? new[] { session.FilePath }
                    : new[] { session.FilePath, session.MeanlinePath, session.OptimalLapPath, session.SessionMetadataPath }
                        .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        .Select(path => path!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                var entry = new PackageSession
                {
                    SessionGuid = session.SessionGuid,
                    Kind = isXrk ? "xrk" : "garmin",
                    Driver = source.Driver,
                    Vehicle = source.Vehicle,
                    TrackName = source.TrackName,
                    Layout = source.Layout,
                    TrackCondition = source.TrackCondition,
                    RaceType = source.RaceType,
                    LocalStart = session.LocalStart,
                    PrimaryFile = Path.GetFileName(session.FilePath)
                };

                foreach (var file in files)
                {
                    var name = Path.GetFileName(file);
                    archive.CreateEntryFromFile(file, $"sessions/{session.SessionGuid}/{name}", CompressionLevel.Optimal);
                    entry.Files.Add(name);
                }

                manifest.Sessions.Add(entry);
            }

            var manifestEntry = archive.CreateEntry(ManifestName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(manifestEntry.Open());
            writer.Write(JsonSerializer.Serialize(manifest, JsonOptions));
        }

        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    public static PackageManifest ReadManifest(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        return ReadManifest(archive);
    }

    private static PackageManifest ReadManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry(ManifestName)
                    ?? throw new InvalidDataException("The package has no manifest.json.");
        using var reader = new StreamReader(entry.Open());
        return JsonSerializer.Deserialize<PackageManifest>(reader.ReadToEnd(), JsonOptions)
               ?? throw new InvalidDataException("The package manifest is empty.");
    }

    /// <summary>
    /// Extracts new sessions into &lt;dataFolder&gt;\Driver\Track\data. Sessions whose primary file name already
    /// exists anywhere under the data folder are skipped so repeat imports are harmless.
    /// </summary>
    public static (PackageManifest Manifest, List<ImportedSession> Imported, int Skipped) Import(
        string packagePath, string dataFolder, ISet<string> existingFileNames)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var manifest = ReadManifest(archive);
        var imported = new List<ImportedSession>();
        var skipped = 0;
        foreach (var entry in manifest.Sessions)
        {
            var primaryName = Path.GetFileName(entry.PrimaryFile);
            if (string.IsNullOrEmpty(primaryName) || existingFileNames.Contains(primaryName))
            {
                skipped++;
                continue;
            }

            var guid = Path.GetFileName(entry.SessionGuid);
            var destination = Path.Combine(
                dataFolder,
                SanitizeSegment(entry.Driver, "Unknown driver"),
                SanitizeSegment(entry.TrackName, "Unknown track"),
                "data");
            Directory.CreateDirectory(destination);

            foreach (var file in entry.Files)
            {
                var name = Path.GetFileName(file);
                var zipEntry = archive.GetEntry($"sessions/{guid}/{name}");
                if (zipEntry is null || string.IsNullOrEmpty(name))
                {
                    continue;
                }

                zipEntry.ExtractToFile(Path.Combine(destination, name), overwrite: false);
                existingFileNames.Add(name);
            }

            var primaryPath = Path.Combine(destination, primaryName);
            if (File.Exists(primaryPath))
            {
                imported.Add(new ImportedSession(entry, primaryPath));
            }
        }

        return (manifest, imported, skipped);
    }

    public static string SanitizeSegment(string? value, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((value ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray())
            .Trim().TrimEnd('.');
        return cleaned.Length == 0 ? fallback : cleaned;
    }
}
