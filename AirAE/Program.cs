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

internal sealed class AirAeSettings
{
    public bool NotificationsEnabled { get; set; } = true;
    public bool DeleteZipAfterExtraction { get; set; }
    public bool StartWithWindows { get; set; } = true;
    public bool Paused { get; set; }
    public string OutputFolder { get; set; } = "";
    public string ExtractionMode { get; set; } = "SeparateFolder";
}

internal sealed class AirAeContext : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly FileSystemWatcher watcher;
    private readonly string downloads;
    private readonly string appFolder;
    private readonly string stateFile;
    private readonly string logFile;
    private readonly string settingsFile;
    private readonly ConcurrentDictionary<string, byte> inProgress = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> completed = new(StringComparer.OrdinalIgnoreCase);
    private AirAeSettings settings = new();
    private MainWindow? mainWindow;

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
        settingsFile = Path.Combine(appFolder, "settings.json");
        LoadSettings();
        LoadState();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Air AE", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Open Air AE settings", null, (_, _) => OpenSettings());
        menu.Items.Add("Extract a ZIP now...", null, (_, _) => ExtractNow());
        menu.Items.Add("Extract all ZIPs now", null, async (_, _) => await ExtractAllAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Downloads", null, (_, _) => OpenFolder(downloads));
        menu.Items.Add("Open extraction history", null, (_, _) => OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        var pauseItem = new ToolStripMenuItem("Pause automatic extraction") { Checked = settings.Paused, CheckOnClick = true };
        pauseItem.Click += (_, _) =>
        {
            settings.Paused = pauseItem.Checked;
            SaveSettings();
            UpdateTrayText();
            Log(settings.Paused ? "Automatic extraction paused." : "Automatic extraction resumed.");
            mainWindow?.RefreshStatus();
        };
        menu.Items.Add(pauseItem);
        var startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = IsStartupEnabled(),
            CheckOnClick = true
        };
        startupItem.Click += (_, _) =>
        {
            settings.StartWithWindows = startupItem.Checked;
            SetStartup(startupItem.Checked);
            SaveSettings();
        };
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
        tray.DoubleClick += (_, _) => ShowMainWindow();

        watcher = new FileSystemWatcher(downloads, "*.zip")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };
        watcher.Created += OnZipChanged;
        watcher.Renamed += (_, e) => QueueZip(e.FullPath);

        if (settings.StartWithWindows && !IsStartupEnabled()) SetStartup(true);
        else if (!settings.StartWithWindows && IsStartupEnabled()) SetStartup(false);

        Log($"Air AE started. Watching: {downloads}");
        UpdateTrayText();
        if (settings.NotificationsEnabled)
            tray.ShowBalloonTip(2500, "Air AE is running", "New ZIP downloads will be extracted automatically.", ToolTipIcon.Info);

        // Existing ZIPs are never scanned at startup. Only new ZIPs are extracted automatically.
        ShowMainWindow();
    }

    private void OnZipChanged(object sender, FileSystemEventArgs e) => QueueZip(e.FullPath);

    private void QueueZip(string path)
    {
        if (settings.Paused || !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return;
        _ = Task.Run(async () => await ProcessZipAsync(path));
    }

    private async Task<bool> ProcessZipAsync(string path)
    {
        if (!inProgress.TryAdd(path, 0)) return false;
        try
        {
            long previousLength = -1;
            int stableChecks = 0;
            for (int i = 0; i < 90 && stableChecks < 4; i++)
            {
                if (!File.Exists(path)) return false;
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
            if (stableChecks < 4 || !File.Exists(path)) return false;

            string fingerprint = GetFingerprint(path);
            lock (completed)
            {
                if (completed.Contains(fingerprint))
                {
                    Log($"Skipped already-extracted ZIP: {Path.GetFileName(path)}");
                    return false;
                }
            }

            using (ZipFile.OpenRead(path)) { }
            string outputRoot = GetOutputFolder();
            Directory.CreateDirectory(outputRoot);
            bool separateFolder = settings.ExtractionMode != "DirectToFolder";
            string destination = separateFolder
                ? MakeUniqueFolder(Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(path)))
                : outputRoot;
            if (separateFolder) Directory.CreateDirectory(destination);

            var createdFiles = new List<string>();
            try
            {
                using var archive = ZipFile.OpenRead(path);
                string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.FullName)) continue;
                    string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Any(p => p == ".."))
                        throw new InvalidDataException("The ZIP contains an unsafe file path.");

                    string outputPath = Path.GetFullPath(Path.Combine(destination, relative));
                    if (!outputPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The ZIP contains a file path outside its destination.");

                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    {
                        Directory.CreateDirectory(outputPath);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    if (!separateFolder) outputPath = MakeUniqueFilePath(outputPath);
                    entry.ExtractToFile(outputPath, overwrite: false);
                    createdFiles.Add(outputPath);
                }
            }
            catch
            {
                if (separateFolder)
                {
                    try { Directory.Delete(destination, recursive: true); } catch { }
                }
                else
                {
                    foreach (string created in createdFiles)
                        try { File.Delete(created); } catch { }
                }
                throw;
            }

            lock (completed)
            {
                completed.Add(fingerprint);
                SaveState();
            }

            if (settings.DeleteZipAfterExtraction)
            {
                try { File.Delete(path); }
                catch (Exception ex) { Log($"Could not delete ZIP {Path.GetFileName(path)}: {ex.Message}"); }
            }

            string resultLocation = separateFolder ? Path.GetFileName(destination) : outputRoot;
            Log($"Extracted {Path.GetFileName(path)} → {resultLocation}");
            Notify("ZIP extracted", $"{Path.GetFileName(path)} was extracted successfully.");
            return true;
        }
        catch (Exception ex)
        {
            Log($"ERROR for {Path.GetFileName(path)}: {ex.Message}");
            Notify("ZIP extraction failed", $"{Path.GetFileName(path)}: {ex.Message}", ToolTipIcon.Warning);
            return false;
        }
        finally
        {
            inProgress.TryRemove(path, out _);
        }
    }

    private string GetOutputFolder() =>
        string.IsNullOrWhiteSpace(settings.OutputFolder) ? downloads : Environment.ExpandEnvironmentVariables(settings.OutputFolder);

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

    private static string MakeUniqueFilePath(string requested)
    {
        if (!File.Exists(requested) && !Directory.Exists(requested)) return requested;
        string folder = Path.GetDirectoryName(requested)!;
        string name = Path.GetFileNameWithoutExtension(requested);
        string extension = Path.GetExtension(requested);
        for (int i = 2; i < 10000; i++)
        {
            string candidate = Path.Combine(folder, $"{name} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Could not find a safe filename for an extracted file.");
    }

    private static string GetFingerprint(string path)
    {
        var info = new FileInfo(path);
        return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(settingsFile))
                settings = JsonSerializer.Deserialize<AirAeSettings>(File.ReadAllText(settingsFile)) ?? new AirAeSettings();
        }
        catch { settings = new AirAeSettings(); }
        if (string.IsNullOrWhiteSpace(settings.OutputFolder)) settings.OutputFolder = downloads;
    }

    private void SaveSettings()
    {
        try { File.WriteAllText(settingsFile, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Log($"Could not save settings: {ex.Message}"); }
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
        catch { Log("Could not read previous extraction history; starting a fresh history."); }
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

    private void Notify(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (settings.NotificationsEnabled && tray.Visible)
            tray.ShowBalloonTip(2200, title, message.Length > 240 ? message[..240] : message, icon);
    }

    private void OpenSettings()
    {
        using var form = new SettingsForm(settings, downloads);
        if (form.ShowDialog() != DialogResult.OK) return;
        settings.NotificationsEnabled = form.NotificationsEnabled;
        settings.DeleteZipAfterExtraction = form.DeleteZipAfterExtraction;
        settings.StartWithWindows = form.StartWithWindows;
        settings.Paused = form.Paused;
        settings.OutputFolder = form.OutputFolder;
        settings.ExtractionMode = form.ExtractionMode;
        SetStartup(settings.StartWithWindows);
        SaveSettings();
        UpdateTrayText();
        mainWindow?.RefreshStatus();
        Log("Settings updated.");
    }

    private void ExtractNow()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a ZIP file to extract",
            Filter = "ZIP archives (*.zip)|*.zip",
            InitialDirectory = downloads,
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == DialogResult.OK)
            _ = Task.Run(async () => await ProcessZipAsync(dialog.FileName));
    }

    private async Task ExtractAllAsync()
    {
        string[] files;
        try { files = Directory.GetFiles(downloads, "*.zip", SearchOption.TopDirectoryOnly); }
        catch (Exception ex) { Notify("Could not scan Downloads", ex.Message, ToolTipIcon.Warning); return; }
        int extracted = 0;
        foreach (string file in files)
            if (await ProcessZipAsync(file)) extracted++;
        Notify("Air AE finished", $"Extracted {extracted} of {files.Length} ZIP file(s).");
    }

    private void ShowMainWindow()
    {
        if (mainWindow == null || mainWindow.IsDisposed)
            mainWindow = new MainWindow(
                OpenSettings, ExtractNow, () => _ = ExtractAllAsync(),
                () => OpenFolder(downloads), OpenLog, TogglePause,
                () => settings.Paused,
                () => settings.ExtractionMode == "DirectToFolder" ? "Direct to destination" : "Separate folder per ZIP",
                GetOutputFolder);
        mainWindow.RefreshStatus();
        if (!mainWindow.Visible) mainWindow.Show();
        if (mainWindow.WindowState == FormWindowState.Minimized) mainWindow.WindowState = FormWindowState.Normal;
        mainWindow.Activate();
    }

    private void TogglePause()
    {
        settings.Paused = !settings.Paused;
        SaveSettings();
        UpdateTrayText();
        Log(settings.Paused ? "Automatic extraction paused." : "Automatic extraction resumed.");
        mainWindow?.RefreshStatus();
    }

    private void UpdateTrayText() =>
        tray.Text = settings.Paused ? "Air AE — Paused" : "Air AE — Watching Downloads";

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

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) { Log($"Could not open folder: {ex.Message}"); }
    }

    private void OpenLog()
    {
        try
        {
            if (!File.Exists(logFile)) File.WriteAllText(logFile, "Air AE extraction history" + Environment.NewLine);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(logFile) { UseShellExecute = true });
        }
        catch { MessageBox.Show($"Activity log: {logFile}", "Air AE"); }
    }

    protected override void ExitThreadCore()
    {
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
        if (mainWindow != null && !mainWindow.IsDisposed) mainWindow.CloseForExit();
        tray.Visible = false;
        tray.Dispose();
        Log("Air AE exited.");
        base.ExitThreadCore();
    }
}

internal sealed class SettingsForm : Form
{
    private readonly CheckBox notifications = new() { Text = "Show notifications", AutoSize = true };
    private readonly CheckBox deleteZip = new() { Text = "Delete ZIP after successful extraction", AutoSize = true };
    private readonly CheckBox startup = new() { Text = "Start Air AE when Windows starts", AutoSize = true };
    private readonly CheckBox paused = new() { Text = "Pause automatic extraction", AutoSize = true };
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    private readonly TextBox folder = new() { ReadOnly = true, Width = 330 };
    private readonly string downloads;

    public bool NotificationsEnabled => notifications.Checked;
    public bool DeleteZipAfterExtraction => deleteZip.Checked;
    public bool StartWithWindows => startup.Checked;
    public bool Paused => paused.Checked;
    public string OutputFolder => folder.Text;
    public string ExtractionMode => mode.SelectedIndex == 1 ? "DirectToFolder" : "SeparateFolder";

    public SettingsForm(AirAeSettings current, string downloadsFolder)
    {
        downloads = downloadsFolder;
        Text = "Air AE Settings";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(470, 390);
        Font = new Font("Segoe UI", 9F);

        var heading = new Label { Text = "Air AE Settings", Font = new Font("Segoe UI", 17F, FontStyle.Bold), AutoSize = true, Location = new Point(22, 18) };
        var subtitle = new Label { Text = "Choose how Air AE handles your ZIP downloads.", AutoSize = true, ForeColor = Color.DimGray, Location = new Point(24, 53) };
        notifications.Location = new Point(25, 92);
        deleteZip.Location = new Point(25, 121);
        startup.Location = new Point(25, 150);
        paused.Location = new Point(25, 179);

        var modeLabel = new Label { Text = "Extraction mode", AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Location = new Point(25, 215) };
        mode.Items.Add("Create a separate folder for each ZIP (default)");
        mode.Items.Add("Put the files directly into the destination folder");
        mode.Location = new Point(25, 239);

        var folderLabel = new Label { Text = "Where extracted files go", AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Location = new Point(25, 277) };
        folder.Location = new Point(25, 301);
        var browse = new Button { Text = "Browse...", Location = new Point(365, 299), Size = new Size(80, 27) };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Choose where Air AE should extract files", SelectedPath = folder.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath;
        };

        notifications.Checked = current.NotificationsEnabled;
        deleteZip.Checked = current.DeleteZipAfterExtraction;
        startup.Checked = current.StartWithWindows;
        paused.Checked = current.Paused;
        folder.Text = string.IsNullOrWhiteSpace(current.OutputFolder) ? downloads : current.OutputFolder;
        mode.SelectedIndex = current.ExtractionMode == "DirectToFolder" ? 1 : 0;

        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(285, 345), Size = new Size(75, 30) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(370, 345), Size = new Size(75, 30) };
        Controls.AddRange(new Control[] { heading, subtitle, notifications, deleteZip, startup, paused, modeLabel, mode, folderLabel, folder, browse, save, cancel });
        AcceptButton = save;
        CancelButton = cancel;
    }
}



internal sealed class MainWindow : Form
{
    private readonly Label status = new();
    private readonly Label details = new();
    private readonly Label statusDescription = new();
    private readonly Button pause = new();
    private readonly Action openSettings, extractOne, extractAll, openDownloads, openHistory, togglePause;
    private readonly Func<bool> isPaused;
    private readonly Func<string> modeText, folderText;
    private bool closingForExit;

    public MainWindow(Action openSettings, Action extractOne, Action extractAll, Action openDownloads, Action openHistory, Action togglePause, Func<bool> isPaused, Func<string> modeText, Func<string> folderText)
    {
        this.openSettings = openSettings; this.extractOne = extractOne; this.extractAll = extractAll;
        this.openDownloads = openDownloads; this.openHistory = openHistory; this.togglePause = togglePause;
        this.isPaused = isPaused; this.modeText = modeText; this.folderText = folderText;

        Text = "Air AE";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(820, 590);
        MinimumSize = new Size(720, 540);
        Font = new Font("Segoe UI", 10F);
        BackColor = Color.FromArgb(242, 245, 250);
        ForeColor = Color.FromArgb(31, 41, 55);

        var header = new Panel { Dock = DockStyle.Top, Height = 112, BackColor = Color.FromArgb(22, 34, 54), Padding = new Padding(28, 18, 28, 14) };
        var brand = new Label { Text = "AIR AE", ForeColor = Color.White, Font = new Font("Segoe UI", 24F, FontStyle.Bold), AutoSize = true, Location = new Point(26, 12) };
        var tagline = new Label { Text = "AIR AUTO EXTRACTOR  •  VERSION 3.0", ForeColor = Color.FromArgb(190, 205, 225), Font = new Font("Segoe UI", 9F, FontStyle.Bold), AutoSize = true, Location = new Point(30, 59) };
        header.Controls.AddRange(new Control[] { brand, tagline });

        var page = new Panel { Dock = DockStyle.Fill, Padding = new Padding(26, 22, 26, 20), AutoScroll = true };
        var intro = new Label { Text = "Your downloads, handled.", Font = new Font("Segoe UI", 19F, FontStyle.Bold), AutoSize = true, Location = new Point(2, 4) };
        var introSub = new Label { Text = "Air AE watches for new ZIP downloads and extracts them when they finish.", ForeColor = Color.FromArgb(100, 112, 130), AutoSize = true, Location = new Point(4, 42) };

        var statusCard = new Panel { Location = new Point(0, 82), Size = new Size(740, 124), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        var statusCaption = new Label { Text = "AUTOMATIC EXTRACTION", Font = new Font("Segoe UI", 8.5F, FontStyle.Bold), ForeColor = Color.FromArgb(110, 122, 140), AutoSize = true, Location = new Point(20, 15) };
        status.Location = new Point(20, 39); status.AutoSize = true; status.Font = new Font("Segoe UI", 19F, FontStyle.Bold);
        statusDescription.Location = new Point(22, 82); statusDescription.AutoSize = true; statusDescription.ForeColor = Color.FromArgb(100, 112, 130);
        statusCard.Controls.AddRange(new Control[] { statusCaption, status, statusDescription });

        var quickTitle = new Label { Text = "QUICK ACTIONS", Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = Color.FromArgb(100, 112, 130), AutoSize = true, Location = new Point(2, 229) };
        var one = MakeButton("Extract one ZIP…", 0, 257, 350, 52, Color.FromArgb(38, 99, 190), Color.White);
        one.Click += (_, _) => extractOne();
        var all = MakeButton("Extract all ZIPs manually", 366, 257, 350, 52, Color.White, Color.FromArgb(31, 41, 55));
        all.Click += (_, _) => extractAll();
        var down = MakeButton("Open Downloads folder", 0, 321, 350, 48, Color.White, Color.FromArgb(31, 41, 55));
        down.Click += (_, _) => openDownloads();
        var history = MakeButton("View extraction history", 366, 321, 350, 48, Color.White, Color.FromArgb(31, 41, 55));
        history.Click += (_, _) => openHistory();

        var settingsCard = new Panel { Location = new Point(0, 389), Size = new Size(716, 88), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        var settingsText = new Label { Text = "PREFERENCES", Font = new Font("Segoe UI", 8.5F, FontStyle.Bold), ForeColor = Color.FromArgb(110, 122, 140), AutoSize = true, Location = new Point(16, 12) };
        details.Location = new Point(16, 39); details.AutoSize = true; details.ForeColor = Color.FromArgb(75, 87, 105); details.Font = new Font("Segoe UI", 9F);
        settingsCard.Controls.AddRange(new Control[] { settingsText, details });

        pause.Location = new Point(0, 493); pause.Size = new Size(350, 42); pause.FlatStyle = FlatStyle.Flat; pause.FlatAppearance.BorderSize = 0; pause.BackColor = Color.FromArgb(225, 234, 246); pause.ForeColor = Color.FromArgb(35, 75, 130); pause.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold); pause.Cursor = Cursors.Hand;
        pause.Click += (_, _) => togglePause();
        var config = MakeButton("Open settings", 366, 493, 350, 42, Color.FromArgb(22, 34, 54), Color.White);
        config.Click += (_, _) => openSettings();

        page.Controls.AddRange(new Control[] { intro, introSub, statusCard, quickTitle, one, all, down, history, settingsCard, pause, config });
        page.Resize += (_, _) => { statusCard.Width = page.ClientSize.Width - 52; settingsCard.Width = page.ClientSize.Width - 52; };
        Controls.Add(page);
        Controls.Add(header);
        FormClosing += (_, e) => { if (!closingForExit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        RefreshStatus();
    }

    private static Button MakeButton(string text, int x, int y, int width, int height, Color background, Color foreground) =>
        new Button { Text = text, Location = new Point(x, y), Size = new Size(width, height), FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderSize = 0 }, BackColor = background, ForeColor = foreground, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), Cursor = Cursors.Hand, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 8, 0) };

    public void RefreshStatus()
    {
        bool stopped = isPaused();
        status.Text = stopped ? "Paused" : "Watching for new ZIPs";
        status.ForeColor = stopped ? Color.DarkOrange : Color.FromArgb(22, 145, 91);
        statusDescription.Text = stopped
            ? "Automatic extraction is paused. Your existing ZIP files are left untouched."
            : "Only ZIPs newly added while Air AE is running are automatically extracted.";
        pause.Text = stopped ? "  Resume automatic extraction" : "  Pause automatic extraction";
        details.Text = "Mode: " + modeText() + "     •     Destination: " + folderText();
    }

    public void CloseForExit() { closingForExit = true; Close(); }
}
