# Air AE (Air Auto Extractor)

Air AE is a small Windows system-tray utility that watches your Downloads folder and automatically extracts completed ZIP downloads.

## Features

- Runs quietly in the Windows system tray and can start with Windows.
- Watches Downloads for ZIP files created while Air AE is running. **Existing ZIP files are not automatically extracted at startup.**
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

Run `AirAE.exe`. Air AE adds itself to the current Windows user's startup applications by default and begins watching Downloads. The main Air AE window opens on launch. You can also open it by double-clicking the tray icon or right-clicking the tray icon and choosing Open Air AE. Existing ZIPs are left alone unless you manually choose Extract a ZIP now or Extract all ZIPs now.

Settings and activity history are stored under `%LOCALAPPDATA%\AirAE`.

## Extraction modes

- **Separate folder (default):** `Downloads\ArchiveName\...`
- **Direct:** extracted files go directly into the selected destination, with safe renamed copies if a file already exists.

## Safety

Air AE extracts ZIP entries only inside the selected destination and rejects paths that attempt to escape it. Existing files are not overwritten. Automatic ZIP deletion is off by default and only happens after a successful extraction. Only ZIP files are supported.
