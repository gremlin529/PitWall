# PitWall

A Windows desktop viewer for Garmin autosport performance, AiM XRK telemetry, session metadata, and meanline JSON files.

## To Get the data files
login your account at garmin.com and then go to account 
pick Data Management, and ask it to send you all your data
When the files are available download them, unzip
Look for the DI_BOOT_AUTOSPORT directory
copy all the files in all of these directories (if you have multiple zip files)
to a single data directory
Launch the app point it at that directory and it'll show you the data!

## Run

Open `TrackTelemetryViewer.csproj` in Visual Studio with the .NET 9 Windows Desktop workload, or run:

```powershell
dotnet run --project .\TrackTelemetryViewer.csproj
```

The viewer automatically looks for a `Data` folder beside the application or project. It searches that folder and all subdirectories for Garmin JSON exports, AiM `.xrk` files, and related data. Use **Open data folder** to select a different data folder.

## Features

- Browse sessions in a track → vehicle → date → session time → lap tree. GPS start locations within 5 miles are grouped as one venue, then separated by meanline configuration so distinct layouts (including clockwise and counter-clockwise directions) appear as separate track nodes. Sessions without a configuration are marked "Layout unspecified" when multiple layouts are present.
- Right-click a "Layout unspecified" track node to choose one of the available layouts at that venue. The choice is saved by session ID in `viewer-settings.json` and reused on the next load.
- Correct an individual session's track/layout in Session Details; the override is saved per session and can be reset to the layout in the source data.
- The session tree starts collapsed, with a driver grouping beneath each vehicle. Vehicle groups show their fastest available optimal lap first (including its lap time and source date/time); optimal laps are purple in the vehicle and source-session nodes, and fastest driven laps are green with their time shown in the expanded session header.
- Drag the divider beside the session library to resize the library and data view. Wet sessions are blue and marked "(wet)" in the tree.
- Use the gear button to choose metric (km/h, km, m) or imperial (mph, mi, ft) units, edit car aliases in the Cars tab, and rename or combine track/layout aliases in the Tracks tab. Assigning different aliases to the same library name combines them into one tree entry. Vehicle visibility and settings are saved in `viewer-settings.json` in the selected data folder (or in the app's local settings folder until a data folder is selected).
- The last successfully loaded data folder is remembered between application launches.
- AiM `.xrk` sessions are added to the same track/session library. GPS position and speed, GPS-derived longitudinal and lateral acceleration in g, track name, driver, vehicle, and lap boundaries are read with the bundled XRK reader. Their GPS traces can be graphed and compared with other sessions.
- Race Studio CSV exports (`AIM CSV File`) are loaded too. They contain no coordinates, so the map is dead-reckoned from GPS speed and heading and the track gets a synthetic location (rename it with track mappings). Pit/in-out laps are skipped, and these sessions can't be exported to XRK.
- The default Channels settings tab lets you show or hide speed, combined and separate g-force plots, lap delta, and each numeric channel found in the loaded XRK files (including RPM, throttle, brake, steering, and other recorded signals). Reorder channels with the up/down arrows; visibility and order are saved in `viewer-settings.json`. XRK channels are aligned to lap distance using their timestamps.
- Select a track to show its meanline map and track statistics in the main window.
- The track overview groups the cars-and-drivers panel by driver, showing each driver's total laps and per-car fastest and optimal lap links.
- Correct a session's vehicle, track condition, or session type in the session details panel. Corrections are saved in `%LOCALAPPDATA%\TrackTelemetryViewer\session-overrides.json`; original Garmin exports are not changed.
- Vehicle and driver names are resolved from the exported catalogs and profile files when their IDs match. The `3F9DDA44…` vehicle alias is identified as a 2023 Porsche GT4, but the vehicle export cannot distinguish its tire/model variant; the vehicle editor offers catalog cars plus custom names when an alias cannot be mapped.
- Right-click a vehicle node to assign a catalog car name. Garmin assignments are saved by metadata alias; XRK assignments are saved by session ID because XRK files do not provide a Garmin vehicle alias. Both are saved in `viewer-settings.json` in the selected data folder and reused the next time it is loaded.
- The track tree nodes have visible expand/collapse controls.
- The track overview counts laps in the background by scanning session telemetry files.
- Track overviews show a separate car-and-driver summary with each driver's session and lap counts plus clickable fastest and optimal laps. Each driver node shows that driver's optimal lap time and opens the optimal lap when selected.
- View every driven lap for each session, with the average lap used if no driven laps are available.
- The selected and reference lap cards identify optimal laps in purple and fastest driven laps in green.
- Right-click a lap and choose **Use as reference lap** to compare it with the selected lap. The GPS traces and speed/acceleration plots are overlaid; the delta is session A elapsed time minus reference elapsed time at the same distance.
- Right-click a session to use its fastest driven lap as the reference; the lap cards identify the selected fastest or optimal lap and its date/time.
- Hover over a plot to move the distance cursor across the plots and GPS map.
- Right-click a date or session and choose **Export…** to write either a `.pitwall` package (a zip with the session files plus a `manifest.json` sidecar holding the corrected driver, vehicle, track layout and condition) or AiM `.xrk` files (one per session; Garmin sessions are converted best-effort from GPS, speed, G-force and laps).
- **Import…** (top bar) unpacks `.pitwall` packages into `<data folder>\<driver>\<track>\data` and applies their corrections. `.pitwall` files placed anywhere in the data folder are unpacked automatically on load; sessions already present are skipped.

The map is an offline GPS trace drawing, not a street-map tile view. Speed units follow the metric/imperial setting. Garmin acceleration X and Y are divided by standard gravity and displayed as longitudinal and lateral g; XRK files provide GPS-derived longitudinal and lateral g. The bundled Windows XRK reader is based on [libxrk](https://github.com/m3rlin45/libxrk). Its license and the licenses for its bundled Rust dependencies are included in `Assets/XrkReader-Third-Party-Notices.txt`. To rebuild it after changing the helper source, install the Rust stable toolchain and run `.\XrkReader\build.ps1`.

## Cross-platform Avalonia version (macOS)

`PitWall.Avalonia/` is a separate Avalonia 11 / .NET 10 app for macOS (it also runs on Windows). It compiles the WPF app's parsing and model code directly (`TelemetryLoader`, `TelemetryModels`, `VboReader`, `AimCsvReader`, `AimCsvAlignment`, `TrackProfileCache`, `PitWallPackage`, `XrkWriter`), and reads and writes the same `viewer-settings.json` and `session-overrides.json`, so both apps can share a data folder.

It follows the WPF app's behavior:

- **Session tree**: same structure, text and colours as WPF. Tracks are bold with a session count. Car, driver and date rows are grey. A driver row shows its optimal time in purple, and selecting it opens that optimal lap. A date row shows "Fastest m:ss.fff", in green only on the driver's fastest day, and selecting it opens that day's fastest lap. A session row shows its start time, duration and fastest time; the time is green only for the day's fastest session, and clicking it opens that lap. Wet sessions are blue and marked "(wet)". Lap lists show the optimal lap in purple and the fastest driven lap in green. Fastest times fill in from a background scan. The first track is selected on load.
- **Context menus**: the same entries as WPF. Laps and drivers have reference lap and clear reference. Sessions have fastest-as-reference, Session details…, assign vehicle and Export…. Dates have fastest-as-reference, assign vehicle and Export…. Cars have assign vehicle. Tracks have Choose layout and the CSV assign/unassign entries. Vehicle menus also have "Other name…".
- **Data view**: lap cards with Fastest/Optimal tags, driver/date/lap/time and vehicle/condition, and a lap delta in green or orange. The cursor readout includes Δ time. The GPS map has segment colouring. Ported WPF charts: legend, cursor values with session-minus-reference deltas, segment boundary lines, an RPM axis in 500 steps and a distance axis.
- **Track overview**: a 3D meanline map that orbits automatically. Drag to orbit, and double-click or wait 10 s to resume. It shows elevation relief and segment colours, and builds a GPS-derived profile for logger-only tracks. Stats, and per-driver/per-car fastest and optimal links with session and lap counts.
- **Data sources and files**: Garmin, AiM `.xrk`, Race Studio CSV (including aligning a CSV venue onto a GPS track) and VBOX `.vbo`. `.pitwall` export/import, and XRK export. Packages in the data folder are unpacked on load.
- **App chrome**: version label and GitHub update check, and remembered window placement.

Checked with a headless Avalonia run against a real 345-session data folder: tree colours and headers, date/session/driver selection, a mouse click on a session's fastest time, reference overlay, every node's context menu, and both export formats. Not yet clicked through by hand on a Mac.

Remaining differences: no custom (borderless) window chrome, and a units drop-down in the header in addition to Settings. Vehicle and layout assignments can't be removed from the tree; clear car mappings in Settings.

Build and run on macOS (needs the .NET 10 SDK and a Rust stable toolchain):

```bash
./XrkReader/build.sh                      # builds the XRK helper for aarch64-apple-darwin (pass a Rust target for others)
dotnet run --project PitWall.Avalonia/PitWall.Avalonia.csproj
```

Publish for Apple Silicon (output includes the `XrkReader` helper and third-party notices):

```bash
./XrkReader/build.sh
dotnet publish PitWall.Avalonia/PitWall.Avalonia.csproj -c Release -r osx-arm64 --self-contained -o PitWall.Avalonia/publish/osx-arm64
./PitWall.Avalonia/publish/osx-arm64/PitWall
```

The output is a plain executable folder, not a signed `.app` bundle or notarized. The helper is staged in `PitWall.Avalonia/Assets/xrk/<rid>/` (git-ignored); without it the app builds with a warning and `.xrk` files fail to load. On Windows the Avalonia project reuses `Assets/XrkReader.exe`.
