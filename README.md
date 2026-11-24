# Air AE (Air Auto Extractor)

Air AE is a small Windows system-tray utility that watches your Downloads folder and automatically extracts completed ZIP downloads.

## Features

- Runs quietly in the Windows system tray and can start with Windows.
- Watches Downloads for new ZIP files and also checks ZIPs already there when it starts.
- Waits for a download to finish before extracting it.
- **Default mode:** creates a separate folder for each ZIP, just like the original version.
- **Direct mode:** extracts the ZIP contents straight into the selected destination folder without creating an extra folder.
- Settings window to turn notifications on or off, pause automatic extraction, choose the extraction mode and destination, control startup, and optionally delete ZIPs after successful extraction.
- Extract one ZIP manually or extract all ZIPs in Downloads.
- Avoids overwriting existing files and folders by choosing unique names.
- Remembers completed archive fingerprints to avoid extracting the same archive again.
- Keeps an activity history and reports extraction errors through the log and optional notifications.
- Rejects ZIP paths that attempt to write outside the chosen destination.
- Keeps the original ZIP by default.

## Build

Requires the .NET 8 SDK on Windows.

```powershell
dotnet publish AirAE/AirAE.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

The GitHub Actions workflow builds a standalone `AirAE.exe` for Windows x64 and uploads it as the `Air-AE-Windows-x64` workflow artifact.

## First launch

Run `AirAE.exe`. Air AE adds itself to the current Windows user's startup applications by default and begins watching Downloads. Right-click its tray icon to open settings, manually extract ZIP files, open Downloads or the activity history, pause extraction, change startup behavior, or exit.

Settings and activity history are stored under `%LOCALAPPDATA%\AirAE`.

## Extraction modes

- **Separate folder (default):** `Downloads\ArchiveName\...`
- **Direct:** extracted files go directly into the selected destination, with safe renamed copies if a file already exists.

## Safety

Air AE extracts ZIP entries only inside the selected destination and rejects paths that attempt to escape it. Existing files are not overwritten. Automatic ZIP deletion is off by default and only happens after a successful extraction. Only ZIP files are supported.
