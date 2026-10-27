using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace TrackTelemetryViewer;

public partial class MainWindow
{
    private readonly List<string> _pendingPackages = [];

    private sealed record PackageImportResult(
        List<ImportedSession> Imported,
        Dictionary<string, string> TrackMappings,
        int Skipped,
        List<string> Failures);

    private MenuItem CreateExportMenuItem(IReadOnlyList<SessionInfo> sessions, string label)
    {
        var item = new MenuItem
        {
            Header = "Export…",
            Style = (Style)FindResource("DarkMenuItemStyle"),
            Tag = new ExportRequest(sessions, label)
        };
        item.Click += Export_Click;
        return item;
    }

    private sealed record ExportRequest(IReadOnlyList<SessionInfo> Sessions, string Label);

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ExportRequest request } || request.Sessions.Count == 0)
        {
            return;
        }

        var sessionText = request.Sessions.Count == 1 ? "1 session" : $"{request.Sessions.Count} sessions";
        var dialog = new ExportDialog(
            $"Export {sessionText} ({request.Label})",
            ExportBaseName(request))
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var sources = request.Sessions.Select(CreateExportSource).ToArray();
        var trackMappings = new Dictionary<string, string>(
            _viewerSettings.TrackMappings, StringComparer.OrdinalIgnoreCase);
        var destination = dialog.Destination;
        var format = dialog.Format;

        BeginLoading("Exporting…");
        try
        {
            var result = await Task.Run(() => format == ExportFormat.PitWall
                ? ExportPackage(sources, trackMappings, destination)
                : ExportXrk(sources, destination));
            MessageBox.Show(this, result, "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(this, exception.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndLoading();
        }
    }

    private string ExportBaseName(ExportRequest request)
    {
        var first = request.Sessions[0];
        var track = _trackBySession.TryGetValue(first.FilePath, out var location) ? location.DisplayName : "session";
        return PitWallPackage.SanitizeSegment($"{request.Label} {track}", "PitWall export");
    }

    private ExportSessionSource CreateExportSource(SessionInfo session)
    {
        var edits = GetSessionEdits(session);
        var track = _trackBySession.TryGetValue(session.FilePath, out var location)
            ? location.DisplayName
            : session.MeanlineTrackName ?? "Unknown track";
        var layout = _viewerSettings.SessionLayoutAssignments.TryGetValue(session.SessionGuid, out var assigned)
            ? assigned
            : "";
        return new ExportSessionSource(
            session, edits.DriverName, edits.VehicleName, track, layout, edits.TrackCondition, edits.RaceType);
    }

    private static string ExportPackage(
        IReadOnlyList<ExportSessionSource> sources,
        IReadOnlyDictionary<string, string> trackMappings,
        string destination)
    {
        if (!destination.EndsWith(PitWallPackage.Extension, StringComparison.OrdinalIgnoreCase))
        {
            destination += PitWallPackage.Extension;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        PitWallPackage.Export(sources, trackMappings, destination);
        return $"Exported {sources.Count} session(s) to:\n{destination}";
    }

    private static string ExportXrk(IReadOnlyList<ExportSessionSource> sources, string folder)
    {
        Directory.CreateDirectory(folder);
        var written = new List<string>();
        var notes = new List<string>();
        foreach (var source in sources)
        {
            var session = source.Session;
            var baseName = PitWallPackage.SanitizeSegment(
                $"{session.LocalStart:yyyyMMdd_HHmmss}_{source.Driver}_{source.TrackName}", "session");
            var path = UniquePath(folder, baseName, ".xrk");
            try
            {
                if (session.AiMData is not null)
                {
                    if (!session.FilePath.EndsWith(".xrk", StringComparison.OrdinalIgnoreCase))
                    {
                        notes.Add($"{session.LocalStart:g}: CSV-sourced sessions can't be exported to XRK.");
                        continue;
                    }

                    File.Copy(session.FilePath, path);
                }
                else
                {
                    XrkWriter.WriteFile(
                        XrkWriter.ReadGarminSession(session, source.Driver, source.Vehicle, source.TrackName), path);
                }

                written.Add(path);
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or IOException)
            {
                notes.Add($"{session.LocalStart:g}: {exception.Message}");
            }
        }

        var message = $"Wrote {written.Count} XRK file(s) to:\n{folder}";
        return notes.Count == 0 ? message : $"{message}\n\nSkipped:\n{string.Join('\n', notes)}";
    }

    private static string UniquePath(string folder, string baseName, string extension)
    {
        var path = Path.Combine(folder, baseName + extension);
        for (var counter = 2; File.Exists(path); counter++)
        {
            path = Path.Combine(folder, $"{baseName} ({counter}){extension}");
        }

        return path;
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_dataFolder is null)
        {
            MessageBox.Show(this, "Open a data folder first. Imported sessions are unpacked into it.",
                "Import", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Import PitWall packages",
            Filter = "PitWall package (*.pitwall)|*.pitwall",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _pendingPackages.AddRange(dialog.FileNames);
        await LoadFolderAsync(_dataFolder);
    }

    // Packages sitting in the data folder are unpacked at load time, plus any chosen with Import.
    private PackageImportResult ExtractPackages(string folder, IReadOnlyList<string> extraPackages)
    {
        var packages = TelemetryLoader.EnumerateFilesRecursively(folder, "*" + PitWallPackage.Extension)
            .Concat(extraPackages)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = new PackageImportResult([], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), 0, []);
        if (packages.Length == 0)
        {
            return result;
        }

        var existing = new HashSet<string>(
            TelemetryLoader.EnumerateFilesRecursively(folder, "*").Select(path => Path.GetFileName(path)!),
            StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            try
            {
                var (manifest, imported, skipped) = PitWallPackage.Import(package, folder, existing);
                result.Imported.AddRange(imported);
                var importedGuids = imported.Select(item => item.Entry.SessionGuid).ToHashSet();
                if (importedGuids.Count > 0)
                {
                    foreach (var (key, value) in manifest.TrackMappings)
                    {
                        result.TrackMappings.TryAdd(key, value);
                    }
                }

                result = result with { Skipped = result.Skipped + skipped };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidDataException or JsonException)
            {
                result.Failures.Add($"{Path.GetFileName(package)}: {exception.Message}");
            }
        }

        return result;
    }

    private void ApplyImportedCorrections(PackageImportResult import)
    {
        if (import.Imported.Count == 0)
        {
            return;
        }

        foreach (var (key, value) in import.TrackMappings)
        {
            _viewerSettings.TrackMappings.TryAdd(key, value);
        }

        foreach (var (entry, primaryPath) in import.Imported)
        {
            var guid = entry.Kind == "xrk" ? TelemetryLoader.ComputeAiMSessionId(primaryPath) : entry.SessionGuid;
            var vehicleKnown = !string.IsNullOrWhiteSpace(entry.Vehicle) &&
                               !entry.Vehicle.StartsWith("Unassigned", StringComparison.OrdinalIgnoreCase);
            if (vehicleKnown)
            {
                _viewerSettings.SessionVehicleMappings[guid] = entry.Vehicle;
                if (!_viewerSettings.CustomVehicleNames.Contains(entry.Vehicle, StringComparer.CurrentCultureIgnoreCase))
                {
                    _viewerSettings.CustomVehicleNames.Add(entry.Vehicle);
                }
            }

            if (!string.IsNullOrWhiteSpace(entry.Layout))
            {
                _viewerSettings.SessionLayoutAssignments[guid] = entry.Layout;
            }

            if (!string.IsNullOrWhiteSpace(entry.Vehicle))
            {
                _sessionEdits[guid] = new SessionEdits(
                    entry.Vehicle, entry.TrackCondition, entry.RaceType, entry.Driver);
            }
        }

        SaveViewerSettings(_viewerSettings, "Imported settings not saved",
            "The corrections for the imported sessions could not be saved to");
        SaveSessionEdits();
    }

    private void ReportImport(PackageImportResult import)
    {
        if (import.Imported.Count == 0 && import.Failures.Count == 0)
        {
            return;
        }

        var message = $"Imported {import.Imported.Count} new session(s).";
        if (import.Skipped > 0)
        {
            message += $"\n{import.Skipped} already present and skipped.";
        }

        if (import.Failures.Count > 0)
        {
            message += $"\n\nFailed:\n{string.Join('\n', import.Failures)}";
        }

        StatusText.Text = message.Split('\n')[0];
        MessageBox.Show(this, message, "Import", MessageBoxButton.OK,
            import.Failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }
}
