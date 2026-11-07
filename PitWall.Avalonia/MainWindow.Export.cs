using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TrackTelemetryViewer;

namespace PitWallApp;

internal sealed record PackageImportResult(
    List<ImportedSession> Imported,
    Dictionary<string, string> TrackMappings,
    int Skipped,
    List<string> Failures);

// .pitwall package and XRK export, plus package import, ported from the WPF MainWindow.Export.cs.
public sealed partial class MainWindow
{
    private async Task ExportAsync(IReadOnlyList<SessionInfo> sessions, string label)
    {
        if (sessions.Count == 0)
        {
            return;
        }

        var sessionText = sessions.Count == 1 ? "1 session" : $"{sessions.Count} sessions";
        var track = _model.LocationOf(sessions[0])?.DisplayName ?? "session";
        var dialog = new ExportDialog(
            $"Export {sessionText} ({label})",
            PitWallPackage.SanitizeSegment($"{label} {track}", "PitWall export"));
        if (await dialog.ShowDialog<bool?>(this) != true)
        {
            return;
        }

        var sources = sessions.Select(CreateExportSource).ToArray();
        var trackMappings = new Dictionary<string, string>(_model.Settings.TrackMappings, StringComparer.OrdinalIgnoreCase);
        var destination = dialog.Destination;
        var asXrk = dialog.IsXrk;
        BeginLoading("Exporting…");
        try
        {
            var result = await Task.Run(() => asXrk
                ? ExportXrk(sources, destination)
                : ExportPackage(sources, trackMappings, destination));
            await Dialogs.ShowMessageAsync(this, "Export complete", result);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or NotSupportedException)
        {
            await Dialogs.ShowMessageAsync(this, "Export failed", exception.Message);
        }
        finally
        {
            EndLoading();
        }
    }

    private ExportSessionSource CreateExportSource(SessionInfo session)
    {
        var edits = _model.GetSessionEdits(session);
        var track = _model.LocationOf(session)?.DisplayName ?? session.MeanlineTrackName ?? "Unknown track";
        var layout = _model.Settings.SessionLayoutAssignments.TryGetValue(session.SessionGuid, out var assigned)
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
            var sourceIsXrz = session.FilePath.EndsWith(".xrz", StringComparison.OrdinalIgnoreCase);
            var path = UniquePath(folder, baseName, sourceIsXrz ? ".xrz" : ".xrk");
            try
            {
                if (session.AiMData is not null)
                {
                    if (!TelemetryLoader.IsAimSessionFile(session.FilePath))
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

    private async Task ImportPackagesAsync()
    {
        if (_dataFolder is null)
        {
            await Dialogs.ShowMessageAsync(this, "Import", "Open a data folder first. Imported sessions are unpacked into it.");
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import PitWall packages",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("PitWall package") { Patterns = ["*.pitwall"] }]
        });
        var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0)
        {
            await LoadFolderAsync(_dataFolder, paths);
        }
    }

    // Packages sitting in the data folder are unpacked at load time, plus any chosen with Import.
    private static PackageImportResult ExtractPackages(string folder, IReadOnlyList<string> extraPackages)
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
                if (imported.Count > 0)
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

    private void ApplyImportedCorrections(
        PackageImportResult import, ViewerSettings settings, Dictionary<string, SessionEdits> edits)
    {
        if (import.Imported.Count == 0 || _dataFolder is null)
        {
            return;
        }

        foreach (var (key, value) in import.TrackMappings)
        {
            settings.TrackMappings.TryAdd(key, value);
        }

        foreach (var (entry, primaryPath) in import.Imported)
        {
            var guid = entry.Kind == "xrk" ? TelemetryLoader.ComputeAiMSessionId(primaryPath) : entry.SessionGuid;
            var vehicleKnown = !string.IsNullOrWhiteSpace(entry.Vehicle) &&
                               !entry.Vehicle.StartsWith("Unassigned", StringComparison.OrdinalIgnoreCase);
            if (vehicleKnown)
            {
                settings.SessionVehicleMappings[guid] = entry.Vehicle;
                if (!settings.CustomVehicleNames.Contains(entry.Vehicle, StringComparer.CurrentCultureIgnoreCase))
                {
                    settings.CustomVehicleNames.Add(entry.Vehicle);
                }
            }

            if (!string.IsNullOrWhiteSpace(entry.Layout))
            {
                settings.SessionLayoutAssignments[guid] = entry.Layout;
            }

            if (!string.IsNullOrWhiteSpace(entry.Vehicle))
            {
                edits[guid] = new SessionEdits(entry.Vehicle, entry.TrackCondition, entry.RaceType, entry.Driver);
            }
        }

        if (SettingsStore.Save(_dataFolder, settings) is { } settingsError)
        {
            _ = Dialogs.ShowMessageAsync(this, "Imported settings not saved", settingsError);
        }

        if (SettingsStore.SaveEdits(edits) is { } editsError)
        {
            _ = Dialogs.ShowMessageAsync(this, "Imported corrections not saved", editsError);
        }
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

        _ = Dialogs.ShowMessageAsync(this, "Import", message);
    }
}

/// <summary>Export format and destination picker (WPF ExportDialog).</summary>
internal sealed class ExportDialog : Window
{
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#94A0AD"));
    private readonly string _defaultFileName;
    private readonly RadioButton _pitWallRadio;
    private readonly RadioButton _xrkRadio;
    private readonly TextBlock _xrkNote;
    private readonly TextBlock _destinationLabel;
    private readonly TextBox _destinationBox = new();

    public ExportDialog(string description, string defaultFileName)
    {
        _defaultFileName = defaultFileName;
        Title = "Export";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _pitWallRadio = new RadioButton
        {
            GroupName = "format",
            IsChecked = true,
            Content = "PitWall package (.pitwall) – one zip with all session data and corrections"
        };
        _xrkRadio = new RadioButton
        {
            GroupName = "format",
            Content = "AiM XRK (.xrk) – one file per session (best-effort conversion)"
        };
        _xrkNote = new TextBlock
        {
            Foreground = Muted,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(22, 0, 0, 0),
            Text = "Converted from GPS, speed, G-forces and laps. Other Garmin channels are not included."
        };
        _destinationLabel = new TextBlock { Text = "Save as", Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 16, 0, 4) };
        _destinationBox.Text = Path.Combine(DocumentsFolder, defaultFileName + PitWallPackage.Extension);
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(browse, 1);
        var export = new Button { Content = "Export", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock { Text = description, FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Format", Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 16, 0, 4) },
                _pitWallRadio,
                _xrkRadio,
                _xrkNote,
                _destinationLabel,
                new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _destinationBox, browse } },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 22, 0, 0),
                    Children = { export, cancel }
                }
            }
        };
        _pitWallRadio.IsCheckedChanged += (_, _) => UpdateFormatUi();
        _xrkRadio.IsCheckedChanged += (_, _) => UpdateFormatUi();
        browse.Click += async (_, _) => await BrowseAsync();
        export.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(Destination))
            {
                await Dialogs.ShowMessageAsync(this, "Export", "Choose where to save the export.");
                return;
            }

            Close(true);
        };
        cancel.Click += (_, _) => Close(false);
        UpdateFormatUi();
    }

    public bool IsXrk => _xrkRadio.IsChecked == true;

    public string Destination => _destinationBox.Text?.Trim() ?? string.Empty;

    private static string DocumentsFolder => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    private void UpdateFormatUi()
    {
        var text = _destinationBox.Text ?? string.Empty;
        if (IsXrk)
        {
            _xrkNote.IsVisible = true;
            _destinationLabel.Text = "Save into folder";
            _destinationBox.Text = Path.GetDirectoryName(text) is { Length: > 0 } directory && Path.HasExtension(text)
                ? directory
                : text;
            if (string.IsNullOrWhiteSpace(_destinationBox.Text))
            {
                _destinationBox.Text = DocumentsFolder;
            }
        }
        else
        {
            _xrkNote.IsVisible = false;
            _destinationLabel.Text = "Save as";
            if (!Path.HasExtension(text))
            {
                _destinationBox.Text = Path.Combine(
                    string.IsNullOrWhiteSpace(text) ? DocumentsFolder : text,
                    _defaultFileName + PitWallPackage.Extension);
            }
        }
    }

    private async Task BrowseAsync()
    {
        var text = Destination;
        var directory = Path.HasExtension(text) ? Path.GetDirectoryName(text) : text;
        var start = !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
            ? await StorageProvider.TryGetFolderFromPathAsync(directory)
            : null;
        if (IsXrk)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose where to save the XRK files",
                SuggestedStartLocation = start
            });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } folder)
            {
                _destinationBox.Text = folder;
            }

            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save PitWall package",
            DefaultExtension = "pitwall",
            SuggestedFileName = Path.GetFileName(text) is { Length: > 0 } name ? name : _defaultFileName + PitWallPackage.Extension,
            SuggestedStartLocation = start,
            FileTypeChoices = [new FilePickerFileType("PitWall package") { Patterns = ["*.pitwall"] }]
        });
        if (file?.TryGetLocalPath() is { } path)
        {
            _destinationBox.Text = path;
        }
    }
}
