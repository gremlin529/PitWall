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

## Cross-platform Avalonia version (macOS) — partial port

`PitWall.Avalonia/` is a separate Avalonia 11 / .NET 10 app that runs on macOS (and, untested, Linux/Windows). The WPF app above is unchanged in behavior. **It is not feature-parity with the WPF app.** It shares `TelemetryLoader.cs` and `TelemetryModels.cs` (Garmin JSON, meanline, catalog, and XRK parsing) with the WPF project.

Implemented: open a data folder (last folder remembered); Garmin and `.xrk` sessions; track → vehicle → driver → date → session → lap tree with lazy lap loading, wet sessions in blue, optimal laps in purple, fastest laps in green; WPF-style venue grouping (sessions within 5 miles merge into one venue, so an XRK session joins the nearest Garmin track name; layouts, "Layout unspecified", `SessionLayoutAssignments` and `TrackMappings` are honored); catalog vehicle names and hidden vehicles; metric/imperial speed; GPS trace map; a scrollable stack of plots for every enabled channel (speed, combined g, longitudinal g, lateral g, delta time, and any numeric XRK channel); reference lap overlay (same track only) with time delta; hover cursor across map and plots; a track overview page for track, car and driver nodes (sessions, laps counted in the background, cars, drivers, length per the units setting, meanline map when a meanline file exists, per-driver and per-car fastest and optimal lap links that select the lap in the tree); fastest lap times on session nodes and the optimal time on driver nodes.

Editing (right-click in the tree, plus a Settings window): assign a layout to an unspecified-layout track; assign a catalog, custom or new car name to a vehicle (Garmin by alias in `VehicleMappings`, XRK by session in `SessionVehicleMappings`); "Edit session details…" (vehicle, driver, track/layout, condition, session type); use a session's fastest lap or any lap as the reference; Settings for units, a Channels tab (show/hide and reorder, same `ChannelVisibility`/`ChannelOrder` keys as WPF; XRK channels are hidden by default), vehicle visibility and custom names, car alias mappings and track-name renames. These use the same `viewer-settings.json` (read from the app-data copy and the data folder, written to the data folder) and `session-overrides.json` (`LocalApplicationData/TrackTelemetryViewer`) as the WPF app. Changing a Garmin session's condition also rewrites its metadata file, as in WPF. The grouping logic was checked against real data; the menus and dialogs have been build-checked but not clicked through in a GUI.

The overview and channel features were verified with a headless run against real data (screenshots and tree/selection checks), not by hand in a GUI. Also ported: the WPF dark styling (forced dark theme, macOS system fonts, header bar, stat cards, rounded panels); the "LOADING TELEMETRY" overlay with WPF's begin/end counter semantics (folder scan, lap and trace loading, overview loads); background fastest-lap headers on date nodes ("Fastest m:ss.fff") and session nodes (green time, purple optimal) that update in place without rebuilding the tree; a draggable splitter between tree and data view; Settings opening on the Channels tab; and tree-state preservation (changing channels only redraws charts; edits and settings that affect tree content rebuild the tree while restoring expanded nodes and selection). The loading overlay, splitter drag, header updates and rebuild preservation were checked in a headless run against real data; real mouse feel, context menus, dialogs and the Settings Save flow were not clicked through in a GUI. Not implemented: custom window chrome, and auto-selecting the first track on load; combined-g reference lines are drawn dashed and not distinguished by axis. Vehicle and layout assignments can't be removed from the tree; clear car mappings in Settings.

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
