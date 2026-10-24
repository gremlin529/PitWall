# Track Telemetry Viewer

A Windows desktop viewer for Garmin autosport performance, session metadata, and meanline JSON files.

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

The viewer automatically looks for a `Data` folder beside the application or project. It searches that folder and all subdirectories for Garmin JSON exports and related files. Use **Open data folder** to select a different data folder.

## Features

- Browse sessions in a track → vehicle → date → session time → lap tree. GPS start locations within 5 miles are grouped as one venue, then separated by meanline configuration so distinct layouts (including clockwise and counter-clockwise directions) appear as separate track nodes. Sessions without a configuration are marked "Layout unspecified" when multiple layouts are present.
- Right-click a "Layout unspecified" track node to choose one of the available layouts at that venue. The choice is saved by session ID in `viewer-settings.json` and reused on the next load.
- The session tree starts collapsed, with a driver grouping beneath each vehicle. Vehicle groups show their fastest available optimal lap first (including its lap time and source date/time); optimal laps are purple in the vehicle and source-session nodes, and fastest driven laps are green with their time shown in the expanded session header.
- Drag the divider beside the session library to resize the library and data view. Wet sessions are blue and marked "(wet)" in the tree.
- Use the gear button to choose metric (km/h, km, m) or imperial (mph, mi, ft) units, edit car aliases in the Cars tab, and rename or combine track/layout aliases in the Tracks tab. Assigning different aliases to the same library name combines them into one tree entry. Vehicle visibility and settings are saved in `viewer-settings.json` in the selected data folder (or in the app's local settings folder until a data folder is selected).
- The last successfully loaded data folder is remembered between application launches.
- Select a track to show its meanline map and track statistics in the main window.
- Correct a session's vehicle, track condition, or session type in the session details panel. Corrections are saved in `%LOCALAPPDATA%\TrackTelemetryViewer\session-overrides.json`; original Garmin exports are not changed.
- Vehicle and driver names are resolved from the exported catalogs and profile files when their IDs match. The `3F9DDA44…` vehicle alias is identified as a 2023 Porsche GT4, but the vehicle export cannot distinguish its tire/model variant; the vehicle editor offers catalog cars plus custom names when an alias cannot be mapped.
- Right-click a vehicle node to assign a catalog car name to its metadata alias. These alias-to-name mappings are saved in `viewer-settings.json` in the selected data folder and reused the next time it is loaded.
- The track tree nodes have visible expand/collapse controls.
- The track overview counts laps in the background by scanning session telemetry files.
- View every driven lap for each session, with the average lap used if no driven laps are available.
- Right-click a lap and choose **Use as reference lap** to compare it with the selected lap. The GPS traces and speed/acceleration plots are overlaid; the delta is session A elapsed time minus reference elapsed time at the same distance.
- Hover over a plot to move the distance cursor across the plots and GPS map.

The map is an offline GPS trace drawing, not a street-map tile view. Speed units follow the metric/imperial setting; acceleration X and Y are divided by standard gravity and displayed as longitudinal and lateral g, respectively.
