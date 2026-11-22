using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Win32;

namespace AirAE;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var app = new AirAeContext();
        Application.Run();
    }
}

internal sealed class AirAeContext : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly FileSystemWatcher watcher;
    private readonly string downloads;
    private readonly string appFolder;
    private readonly string stateFile;
    private readonly string logFile;
    private readonly ConcurrentDictionary<string, byte> inProgress = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> completed = new(StringComparer.OrdinalIgnoreCase);
    private bool paused;

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Air AE";

    public AirAeContext()
    {
        downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        appFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirAE");
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(appFolder);
        stateFile = Path.Combine(appFolder, "completed.json");
        logFile = Path.Combine(appFolder, "activity.log");
        LoadState();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Downloads", null, (_, _) => OpenDownloads());
        menu.Items.Add("Open activity log", null, (_, _) => OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        var pauseItem = new ToolStripMenuItem("Pause automatic extraction");
        pauseItem.Click += (_, _) =>
        {
            paused = !paused;
            pauseItem.Text = paused ? "Resume automatic extraction" : "Pause automatic extraction";
            tray.Text = paused ? "Air AE — Paused" : "Air AE — Watching Downloads";
            Log(paused ? "Automatic extraction paused." : "Automatic extraction resumed.");
        };
        menu.Items.Add(pauseItem);
        var startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = IsStartupEnabled(),
            CheckOnClick = true
        };
        startupItem.Click += (_, _) => SetStartup(startupItem.Checked);
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit Air AE", null, (_, _) => ExitThread());

        tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Air AE — Watching Downloads",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (_, _) => OpenDownloads();

        watcher = new FileSystemWatcher(downloads, "*.zip")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };
        watcher.Created += OnZipChanged;
        watcher.Changed += OnZipChanged;
        watcher.Renamed += (_, e) => QueueZip(e.FullPath);

        // Enable startup automatically on first run; never requires a PowerShell window.
        if (!IsStartupEnabled()) SetStartup(true);
        Log($"Air AE started. Watching: {downloads}");
        tray.ShowBalloonTip(2500, "Air AE is running", "New ZIP downloads will be extracted automatically.", ToolTipIcon.Info);
    }

    private void OnZipChanged(object sender, FileSystemEventArgs e) => QueueZip(e.FullPath);

    private void QueueZip(string path)
    {
        if (paused || !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return;
        if (!inProgress.TryAdd(path, 0)) return;
        _ = Task.Run(async () =>
        {
            try { await ProcessZipAsync(path); }
            catch (Exception ex) { Log($"ERROR for {Path.GetFileName(path)}: {ex.Message}"); }
            finally { inProgress.TryRemove(path, out _); }
        });
    }

    private async Task ProcessZipAsync(string path)
    {
        // Wait for the browser/download process to finish writing the archive.
        long previousLength = -1;
        int stableChecks = 0;
        for (int i = 0; i < 90 && stableChecks < 4; i++)
        {
            if (!File.Exists(path)) return;
            try
            {
                long length = new FileInfo(path).Length;
                if (length > 0 && length == previousLength) stableChecks++;
                else stableChecks = 0;
                previousLength = length;
            }
            catch { stableChecks = 0; }
            await Task.Delay(1000);
        }
        if (stableChecks < 4 || !File.Exists(path)) return;

        string fingerprint = GetFingerprint(path);
        lock (completed)
        {
            if (completed.Contains(fingerprint)) return;
        }

        // Confirm it is a readable ZIP before creating an output folder.
        using (ZipFile.OpenRead(path)) { }
        string baseName = Path.GetFileNameWithoutExtension(path);
        string destination = MakeUniqueFolder(Path.Combine(downloads, baseName));
        Directory.CreateDirectory(destination);

        try
        {
            using var archive = ZipFile.OpenRead(path);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/')) continue;
                string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Any(p => p == ".."))
                    throw new InvalidDataException("The ZIP contains an unsafe file path.");

                string outputPath = Path.GetFullPath(Path.Combine(destination, relative));
                string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
                if (!outputPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The ZIP contains a file path outside its destination.");

                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(outputPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                entry.ExtractToFile(outputPath, overwrite: false);
            }
        }
        catch
        {
            try { Directory.Delete(destination, recursive: true); } catch { }
            throw;
        }

        lock (completed)
        {
            completed.Add(fingerprint);
            SaveState();
        }
        Log($"Extracted {Path.GetFileName(path)} → {Path.GetFileName(destination)}");
        tray.ShowBalloonTip(1800, "ZIP extracted", Path.GetFileName(path), ToolTipIcon.Info);
    }

    private static string MakeUniqueFolder(string requested)
    {
        if (!Directory.Exists(requested) && !File.Exists(requested)) return requested;
        for (int i = 2; i < 10000; i++)
        {
            string candidate = requested + " (" + i + ")";
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
        throw new IOException("Could not create a unique extraction folder.");
    }

    private static string GetFingerprint(string path)
    {
        var info = new FileInfo(path);
        return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    private void LoadState()
    {
        try
        {
            if (File.Exists(stateFile))
            {
                var saved = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(stateFile));
                if (saved != null) foreach (var item in saved) completed.Add(item);
            }
        }
        catch { Log("Could not read the previous extraction history; starting a fresh history."); }
    }

    private void SaveState()
    {
        try { File.WriteAllText(stateFile, JsonSerializer.Serialize(completed)); }
        catch (Exception ex) { Log($"Could not save extraction history: {ex.Message}"); }
    }

    private void Log(string message)
    {
        try { File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}"); }
        catch { }
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(AppName) is string;
        }
        catch { return false; }
    }

    private void SetStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key?.SetValue(AppName, $"\"{Application.ExecutablePath}\"");
            else key?.DeleteValue(AppName, false);
            Log(enabled ? "Start with Windows enabled." : "Start with Windows disabled.");
        }
        catch (Exception ex)
        {
            Log($"Could not change Windows startup setting: {ex.Message}");
            MessageBox.Show($"Could not update the Windows startup setting.\n{ex.Message}", "Air AE", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenDownloads()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(downloads) { UseShellExecute = true }); }
        catch (Exception ex) { Log($"Could not open Downloads: {ex.Message}"); }
    }

    private void OpenLog()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(logFile) { UseShellExecute = true }); }
        catch { MessageBox.Show($"Activity log: {logFile}", "Air AE"); }
    }

    protected override void ExitThreadCore()
    {
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
        tray.Visible = false;
        tray.Dispose();
        Log("Air AE exited.");
        base.ExitThreadCore();
    }
}
