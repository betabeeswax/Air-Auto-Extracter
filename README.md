# Air AE (Air Auto Extractor)

Air AE is a small Windows system-tray utility that watches your Downloads folder and automatically extracts completed ZIP downloads.

## Features

- Runs quietly in the Windows system tray.
- Enables automatic startup when first launched.
- Watches the current user's Downloads folder.
- Waits for a ZIP download to finish before opening it.
- Extracts each archive into its own new folder beside the ZIP.
- Avoids overwriting existing folders and files.
- Remembers completed archive fingerprints to avoid repeat extraction.
- Includes tray controls to pause/resume, open Downloads, open the activity log, change startup behavior, and exit.

## Build

Requires the .NET 8 SDK on Windows.

```powershell
dotnet publish AirAE/AirAE.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

The GitHub Actions workflow builds a standalone `AirAE.exe` for Windows x64 and uploads it as the `Air-AE-Windows-x64` workflow artifact.

## First launch

Run `AirAE.exe`. Air AE adds itself to the current Windows user's startup applications and begins watching the Downloads folder. Right-click its tray icon to pause extraction, open the activity log, change startup behavior, or exit.

Settings and activity history are stored under `%LOCALAPPDATA%\AirAE`.

## Safety

Air AE extracts ZIP entries only inside a new destination folder and rejects paths that attempt to escape that folder. It does not delete the original ZIP. Only ZIP files are handled in this initial version.
