using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Input;
using System.Windows.Threading;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Net.Http;
using System.Text;
using Path = System.IO.Path;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using MessageBox = System.Windows.MessageBox;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace DiskSentinel;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly DispatcherTimer _metricsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly string _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskSentinel");
    private readonly ObservableCollection<DriveRow> _drives = [];
    private Dictionary<string, FileState> _snapshot = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private string? CurrentDirectory;
    private ulong _lastIdle, _lastKernel, _lastUser, _lastReceived, _lastSent;
    private bool _hasCpuSample, _hasNetworkSample;
    private bool _loadingModel;
    private bool _allowClose;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private ulong _sessionReceived, _sessionSent;

    public MainWindow()
    {
        InitializeComponent();
        InitializeTrayIcon();
        DriveGrid.ItemsSource = _drives;
        ManageDrivePicker.ItemsSource = _drives;
        HostValue.Text = Environment.MachineName;
        _metricsTimer.Tick += (_, _) => UpdateMetrics();
        _timer.Tick += async (_, _) => await ScanAsync();
        Loaded += async (_, _) =>
        {
            Directory.CreateDirectory(_dataDir);
            var baseline = Path.Combine(_dataDir, "baseline.json");
            try { if (File.Exists(baseline)) _snapshot = await Task.Run(() => JsonSerializer.Deserialize<Dictionary<string, FileState>>(File.ReadAllText(baseline)) ?? new(StringComparer.OrdinalIgnoreCase)); } catch { }
            var directoryBaseline = Path.Combine(_dataDir, "directories.json");
            try { if (File.Exists(directoryBaseline)) _directories = await Task.Run(() => JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(directoryBaseline)) ?? new(StringComparer.OrdinalIgnoreCase)); } catch { }
            LoadModelConfig();
            LoadAIHistory();
            UpdateMetrics(); _metricsTimer.Start();
            await ScanAsync(); _timer.Start();
        };
    }

    private void InitializeTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开磁盘哨兵", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());
        System.Drawing.Icon icon;
        try { icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Information; }
        catch { icon = System.Drawing.SystemIcons.Information; }
        _trayIcon = new System.Windows.Forms.NotifyIcon { Icon = icon, Text = "磁盘哨兵 - 正在后台监控", ContextMenuStrip = menu, Visible = true };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _allowClose = true;
        if (_trayIcon is not null) { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; }
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
        ShowInTaskbar = false;
        _trayIcon?.ShowBalloonTip(1500, "磁盘哨兵仍在运行", "程序已最小化到系统托盘；右键托盘图标可退出。", System.Windows.Forms.ToolTipIcon.Info);
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        await _scanGate.WaitAsync();
        NavStatus.Text = "正在扫描所有本地磁盘…";
        ChangeTotal.Text = "扫描中…";
        try
        {
            var result = await Task.Run(ScanAllDrives);
            var oldSnapshot = _snapshot;
            var selectedRoot = (DriveGrid.SelectedItem as DriveRow)?.Root ?? result.Drives.FirstOrDefault()?.Root;
            var processed = await Task.Run(() =>
            {
                var changes = Compare(oldSnapshot, result.Files);
                File.WriteAllText(Path.Combine(_dataDir, "baseline.json"), JsonSerializer.Serialize(result.Files));
                SaveChanges(changes);
                File.WriteAllText(Path.Combine(_dataDir, "directories.json"), JsonSerializer.Serialize(result.Directories));
                return (changes: changes, files: TopFiles(result.Files, result.Directories, selectedRoot));
            });
            _snapshot = result.Files;
            _directories = result.Directories;
            _drives.Clear();
            foreach (var d in result.Drives) _drives.Add(d);
            UsedTotal.Text = Human(result.Drives.Sum(x => x.Used));
            FreeTotal.Text = Human(result.Drives.Sum(x => x.Free));
            HomeDiskUsed.Text = UsedTotal.Text;
            HomeDiskFree.Text = FreeTotal.Text;
            ChangeTotal.Text = processed.changes.Count == 0 ? "无变化" : $"{(processed.changes.Sum(c => c.Delta) >= 0 ? "+" : "−")}{Human(Math.Abs(processed.changes.Sum(c => c.Delta)))}";
            NavStatus.Text = $"{result.Drives.Count} 个磁盘 · 每 5 分钟更新";
            if (DriveGrid.SelectedItem is DriveRow selected) FilesGrid.ItemsSource = processed.files;
            else if (_drives.Count > 0) { DriveGrid.SelectedIndex = 0; FilesGrid.ItemsSource = processed.files; }
        }
        catch (Exception ex) { NavStatus.Text = "扫描失败：" + ex.Message; ChangeTotal.Text = "扫描失败"; }
        finally { _scanGate.Release(); }
    }

    private static ScanResult ScanAllDrives()
    {
        var files = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var drives = new List<DriveRow>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType == DriveType.Network || d.DriveType == DriveType.CDRom) continue;
                var info = new DirectoryInfo(d.RootDirectory.FullName);
                foreach (var entry in Walk(info))
                {
                    try
                    {
                        if (entry is DirectoryInfo) directories.Add(entry.FullName);
                        else if (entry is FileInfo f) files[f.FullName] = new FileState(f.Length, f.LastWriteTimeUtc);
                    }
                    catch { }
                }
                var total = d.TotalSize;
                var free = d.AvailableFreeSpace;
                drives.Add(new DriveRow(d.Name, d.DriveFormat, total - free, total, free));
            }
            catch { }
        }
        return new ScanResult(drives, files, directories);
    }

    private static IEnumerable<FileSystemInfo> Walk(DirectoryInfo root)
    {
        var pending = new Stack<DirectoryInfo>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            yield return dir;
            FileInfo[] found; try { found = dir.GetFiles(); } catch { continue; }
            foreach (var f in found) yield return f;
            DirectoryInfo[] children; try { children = dir.GetDirectories(); } catch { continue; }
            foreach (var child in children) { try { if ((child.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(child); } catch { } }
        }
    }

    private List<ChangeRow> Compare(Dictionary<string, FileState> before, Dictionary<string, FileState> after)
    {
        var changes = new List<ChangeRow>();
        var addedByFolder = after.Keys.Where(k => !before.ContainsKey(k)).GroupBy(ParentPath).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in after)
        {
            if (!before.TryGetValue(pair.Key, out var old)) { if (before.Count > 0) changes.Add(new(pair.Key, pair.Value.Length, "新增 / 写入", GuessCause(pair.Key, addedByFolder.GetValueOrDefault(ParentPath(pair.Key), 0)), DateTime.Now)); }
            else if (old.Length != pair.Value.Length) changes.Add(new(pair.Key, pair.Value.Length - old.Length, pair.Value.Length > old.Length ? "文件增长" : "文件缩小", GuessCause(pair.Key, 1), DateTime.Now));
        }
        foreach (var pair in before) if (!after.ContainsKey(pair.Key)) changes.Add(new(pair.Key, -pair.Value.Length, "删除", "推测：文件被删除（依据：扫描前后文件清单）", DateTime.Now));
        return changes;
    }

    private static string ParentPath(string path) => Path.GetDirectoryName(path) ?? "";

    private static string GuessCause(string path, int sameFolderAdds)
    {
        var p = path.Replace('/', '\\');
        var ext = Path.GetExtension(p).ToLowerInvariant();
        if (p.Contains("\\Downloads\\", StringComparison.OrdinalIgnoreCase) || p.Contains("\\下载\\", StringComparison.OrdinalIgnoreCase))
            return "推测：浏览器下载（依据：下载目录路径；未确认浏览器进程）";
        if (p.Contains("\\Program Files\\", StringComparison.OrdinalIgnoreCase) || p.Contains("\\Program Files (x86)\\", StringComparison.OrdinalIgnoreCase) || p.Contains("\\Windows\\Installer\\", StringComparison.OrdinalIgnoreCase) || p.Contains("\\AppData\\Local\\Programs\\", StringComparison.OrdinalIgnoreCase))
            return "推测：软件安装/更新（依据：常见安装目录；未确认安装进程）";
        if (sameFolderAdds >= 5 && ext is not ".zip" and not ".7z" and not ".rar" and not ".exe" and not ".msi")
            return "推测：批量解压（依据：同目录短时新增多个文件）";
        if (ext is ".crdownload" or ".part" or ".download" or ".tmp")
            return "推测：下载中的临时文件（依据：文件扩展名）";
        return "来源未能推测";
    }

    private void SaveChanges(List<ChangeRow> changes)
    {
        if (changes.Count == 0) return;
        var path = Path.Combine(_dataDir, $"changes-{DateTime.Now:yyyy-MM}.jsonl");
        using var writer = new StreamWriter(path, append: true);
        foreach (var c in changes) writer.WriteLine(JsonSerializer.Serialize(c));
    }

    private static List<FileRow> TopFiles(Dictionary<string, FileState> snapshot, HashSet<string> directories, string? root)
    {
        if (root is null) return [];
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var result = new Dictionary<string, FileRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in directories.Where(d => string.Equals(ParentPath(d).TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
            result[dir] = new FileRow(Path.GetFileName(dir), "文件夹", 0, Directory.GetLastWriteTimeUtc(dir), true, dir);
        foreach (var pair in snapshot.Where(x => x.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            var relative = pair.Key[prefix.Length..];
            var slash = relative.IndexOfAny(['\\', '/']);
            if (slash < 0) result[pair.Key] = new FileRow(Path.GetFileName(pair.Key), Path.GetExtension(pair.Key).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "文件", pair.Value.Length, pair.Value.Modified, false, pair.Key);
            else
            {
                var dirPath = Path.Combine(root, relative[..slash]);
                if (result.TryGetValue(dirPath, out var row)) result[dirPath] = row with { Size = row.Size + pair.Value.Length };
                else result[dirPath] = new FileRow(relative[..slash], "文件夹", pair.Value.Length, pair.Value.Modified, true, dirPath);
            }
        }
        return result.Values.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private async void DriveGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveGrid.SelectedItem is DriveRow d)
        {
            if (DriveGrid.SelectedItem is DriveRow current && current.Root == d.Root)
            {
                await ShowDirectory(d.Root);
                PageSubtitle.Text = $"{d.Root} · 双击文件夹继续浏览";
            }
        }
    }

    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        HomePage.Visibility = Visibility.Collapsed; DiskPage.Visibility = Visibility.Collapsed; ManagePage.Visibility = Visibility.Collapsed; ModelPage.Visibility = Visibility.Collapsed; AIHistoryPage.Visibility = Visibility.Collapsed; ScanButton.Visibility = Visibility.Collapsed;
        DiskSummaryCards.Visibility = Visibility.Collapsed;
        LogsPage.Visibility = Visibility.Visible;
        HomeNavButton.Background = Brushes.Transparent; HomeNavButton.Foreground = new SolidColorBrush(Color.FromRgb(184, 194, 213));
        DiskNavButton.Background = Brushes.Transparent; DiskNavButton.Foreground = new SolidColorBrush(Color.FromRgb(184, 194, 213));
        LogsNavButton.Background = new SolidColorBrush(Color.FromRgb(38, 52, 82)); LogsNavButton.Foreground = Brushes.White;
        SetNav(LogsNavButton);
        PageTitle.Text = "变化日志";
        PageSubtitle.Text = "来源栏为规则推测，会显示判断依据；不是进程级确认";
        var logPath = Path.Combine(_dataDir, $"changes-{DateTime.Now:yyyy-MM}.jsonl");
        var rows = new List<ChangeRow>();
        if (File.Exists(logPath)) foreach (var line in File.ReadLines(logPath).TakeLast(3000)) { try { var row = JsonSerializer.Deserialize<ChangeRow>(line); if (row != null) rows.Add(row); } catch { } }
        LogsGrid.ItemsSource = rows.OrderByDescending(r => r.Time).Select(r => new LogDisplay(r.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), (r.Delta >= 0 ? "+" : "−") + Human(Math.Abs(r.Delta)), r.Kind, r.Cause, r.Path)).ToList();
    }

    private void Disk_Click(object sender, RoutedEventArgs e)
    {
        HomePage.Visibility = Visibility.Collapsed; LogsPage.Visibility = Visibility.Collapsed; ManagePage.Visibility = Visibility.Collapsed; ModelPage.Visibility = Visibility.Collapsed; AIHistoryPage.Visibility = Visibility.Collapsed; DiskPage.Visibility = Visibility.Visible; ScanButton.Visibility = Visibility.Visible;
        DiskSummaryCards.Visibility = Visibility.Visible;
        HomeNavButton.Background = Brushes.Transparent; HomeNavButton.Foreground = new SolidColorBrush(Color.FromRgb(184, 194, 213));
        DiskNavButton.Background = new SolidColorBrush(Color.FromRgb(38, 52, 82)); DiskNavButton.Foreground = Brushes.White;
        LogsNavButton.Background = Brushes.Transparent; LogsNavButton.Foreground = new SolidColorBrush(Color.FromRgb(184, 194, 213));
        SetNav(DiskNavButton);
        PageTitle.Text = "磁盘空间";
        if (DriveGrid.SelectedItem is DriveRow d) PageSubtitle.Text = $"{d.Root} · 双击文件夹继续浏览";
        else PageSubtitle.Text = "自动发现并监控所有可用磁盘";
    }

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        HomePage.Visibility = Visibility.Visible; DiskPage.Visibility = Visibility.Collapsed; LogsPage.Visibility = Visibility.Collapsed; ManagePage.Visibility = Visibility.Collapsed; ModelPage.Visibility = Visibility.Collapsed; AIHistoryPage.Visibility = Visibility.Collapsed; ScanButton.Visibility = Visibility.Collapsed;
        DiskSummaryCards.Visibility = Visibility.Collapsed;
        HomeNavButton.Background = new SolidColorBrush(Color.FromRgb(38, 52, 82)); HomeNavButton.Foreground = Brushes.White;
        DiskNavButton.Background = Brushes.Transparent; DiskNavButton.Foreground = new SolidColorBrush(Color.FromRgb(184, 194, 213));
        LogsNavButton.Background = Brushes.Transparent; LogsNavButton.Foreground = new SolidColorBrush(Color.FromRgb(184, 194, 213));
        SetNav(HomeNavButton);
        PageTitle.Text = "首页"; PageSubtitle.Text = "设备状态与实时运行数据";
    }

    private async void Manage_Click(object sender, RoutedEventArgs e)
    {
        HomePage.Visibility = Visibility.Collapsed; DiskPage.Visibility = Visibility.Collapsed; LogsPage.Visibility = Visibility.Collapsed; ModelPage.Visibility = Visibility.Collapsed; AIHistoryPage.Visibility = Visibility.Collapsed; ManagePage.Visibility = Visibility.Visible;
        DiskSummaryCards.Visibility = Visibility.Collapsed; ScanButton.Visibility = Visibility.Collapsed;
        SetNav(ManageNavButton); PageTitle.Text = "磁盘管理"; PageSubtitle.Text = "从盘符开始浏览；勾选项目可多选处理";
        if (DriveGrid.SelectedItem is DriveRow drive)
        {
            ManageDrivePicker.SelectedItem = drive;
            await ShowDirectory(drive.Root);
        }
        ManagePath.Text = CurrentDirectory ?? "扫描完成后选择盘符";
    }

    private void Model_Click(object sender, RoutedEventArgs e)
    {
        HomePage.Visibility = Visibility.Collapsed; DiskPage.Visibility = Visibility.Collapsed; LogsPage.Visibility = Visibility.Collapsed; ManagePage.Visibility = Visibility.Collapsed; AIHistoryPage.Visibility = Visibility.Collapsed; ModelPage.Visibility = Visibility.Visible;
        DiskSummaryCards.Visibility = Visibility.Collapsed; ScanButton.Visibility = Visibility.Collapsed;
        SetNav(ModelNavButton); PageTitle.Text = "大模型接入"; PageSubtitle.Text = "配置兼容 OpenAI 的模型 API";
        LoadModelConfig();
    }

    private void AIHistory_Click(object sender, RoutedEventArgs e)
    {
        HomePage.Visibility = Visibility.Collapsed; DiskPage.Visibility = Visibility.Collapsed; LogsPage.Visibility = Visibility.Collapsed; ManagePage.Visibility = Visibility.Collapsed; ModelPage.Visibility = Visibility.Collapsed; AIHistoryPage.Visibility = Visibility.Visible;
        DiskSummaryCards.Visibility = Visibility.Collapsed; ScanButton.Visibility = Visibility.Collapsed;
        SetNav(AIHistoryNavButton); PageTitle.Text = "模型记录"; PageSubtitle.Text = "查看每次文件预测发送的内容、模型回复和用量";
        LoadAIHistory();
    }

    private void AIHistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AIHistoryGrid.SelectedItem is not AIHistoryDisplay record) { AIHistoryPrompt.Text = ""; AIHistoryResponse.Text = ""; return; }
        AIHistoryPrompt.Text = record.Prompt;
        AIHistoryResponse.Text = record.Response;
    }

    private void LoadAIHistory()
    {
        var records = new List<AIRequestRecord>();
        try
        {
            if (File.Exists(AIHistoryPath))
                foreach (var line in File.ReadLines(AIHistoryPath).TakeLast(1000))
                    try { var row = JsonSerializer.Deserialize<AIRequestRecord>(line); if (row is not null) records.Add(row); } catch { }
        }
        catch { }
        var display = records.OrderByDescending(x => x.Time).Select(x => new AIHistoryDisplay(x.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), x.Provider, x.Model, TokenText(x.InputTokens), TokenText(x.OutputTokens), TokenText(x.TotalTokens), x.Prompt, x.Response)).ToList();
        AIHistoryGrid.ItemsSource = display;
        AIHistorySummary.Text = $"共 {display.Count} 次预测记录 · 仅本机保存";
        if (display.Count == 0) { AIHistoryPrompt.Text = ""; AIHistoryResponse.Text = ""; }
    }

    private async void ClearAIHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("确定清空本机保存的所有模型预测记录吗？", "清空模型记录", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { if (File.Exists(AIHistoryPath)) File.Delete(AIHistoryPath); LoadAIHistory(); }
        catch (Exception ex) { MessageBox.Show("清空失败：" + ex.Message, "模型记录", MessageBoxButton.OK, MessageBoxImage.Error); }
        await Task.CompletedTask;
    }

    private static string TokenText(int? count) => count?.ToString("N0") ?? "未提供";

    private async void ManageDrive_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ManagePage.Visibility != Visibility.Visible || ManageDrivePicker.SelectedItem is not DriveRow drive) return;
        DriveGrid.SelectedItem = drive;
        await ShowDirectory(drive.Root);
    }

    private async void ManageUp_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDirectory is null || ManageDrivePicker.SelectedItem is not DriveRow drive) return;
        var parent = Directory.GetParent(CurrentDirectory)?.FullName;
        if (parent is null || !parent.StartsWith(drive.Root, StringComparison.OrdinalIgnoreCase)) parent = drive.Root;
        await ShowDirectory(parent);
    }

    private void Provider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _loadingModel || ProviderInput.SelectedItem is not ComboBoxItem selected) return;
        var provider = selected.Content?.ToString() ?? "";
        EndpointInput.Text = provider switch
        {
            "GPT / OpenAI" => "https://api.openai.com/v1",
            "Claude" => "https://api.anthropic.com",
            "Gemini" => "https://generativelanguage.googleapis.com/v1beta",
            "DeepSeek" => "https://api.deepseek.com/v1",
            "Grok / xAI" => "https://api.x.ai/v1",
            _ => ""
        };
        ModelInput.Text = provider switch { "GPT / OpenAI" => "gpt-5.4-mini", "Claude" => "claude-sonnet-5", "Gemini" => "gemini-3.8-flash", "DeepSeek" => "deepseek-flash", "Grok / xAI" => "grok-4.6", _ => "" };
    }

    private void SetNav(System.Windows.Controls.Button active)
    {
        foreach (var button in new[] { HomeNavButton, DiskNavButton, ManageNavButton, ModelNavButton, AIHistoryNavButton, LogsNavButton })
        { button.Background = Brushes.Transparent; button.Foreground = new SolidColorBrush(Color.FromRgb(184, 194, 213)); }
        active.Background = new SolidColorBrush(Color.FromRgb(38, 52, 82)); active.Foreground = Brushes.White;
    }

    private async void SaveModel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var existing = ReadModelConfig();
            var key = string.IsNullOrWhiteSpace(ApiKeyInput.Password) ? existing?.ProtectedKey ?? "" : ProtectSecret(ApiKeyInput.Password);
            var provider = (ProviderInput.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "OpenAI 兼容 / 自定义";
            var config = new ModelConfig(EndpointInput.Text.Trim(), ModelInput.Text.Trim(), key, provider);
            File.WriteAllText(ModelConfigPath, JsonSerializer.Serialize(config));
            ApiKeyInput.Clear(); ModelStatus.Text = "连接配置已加密保存到本机用户配置";
        }
        catch (Exception ex) { ModelStatus.Text = "保存失败：" + ex.Message; }
        await Task.CompletedTask;
    }

    private async void TestModel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveModel_Click(sender, e);
            var response = await CallModelAsync("只回复 OK");
            ModelStatus.Text = "连接成功：" + response.Response.Trim();
        }
        catch (Exception ex) { ModelStatus.Text = "连接失败：" + ex.Message; }
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CurrentDirectory) || !Directory.Exists(CurrentDirectory))
        { AnalysisStatus.Text = "请先选择要分析的目录"; return; }
        var config = ReadModelConfig();
        if (config is null || string.IsNullOrWhiteSpace(config.Model) || string.IsNullOrWhiteSpace(config.ProtectedKey))
        {
            AnalysisStatus.Text = "请先配置模型连接"; Model_Click(sender, e); return;
        }
        // Use the same immediate-child rows that are currently shown in the file manager.
        // This keeps hidden/system entries and stale scan results from silently diverging
        // from what the user can inspect in the directory list.
        var visibleRows = ManageFilesGrid.ItemsSource?.Cast<FileRow>().ToList() ?? [];
        var paths = visibleRows.Where(x => !x.IsDirectory).Select(x => x.FullPath).ToArray();
        AnalysisGrid.ItemsSource = null;
        if (paths.Length == 0) { AnalysisStatus.Text = "当前目录列表中没有直接包含的文件；子文件夹不会递归分析"; return; }
        var info = paths.Select(p => { try { var f = new FileInfo(p); return $"• {p} | {Human(f.Length)} | {f.LastWriteTime:yyyy-MM-dd HH:mm}"; } catch { return $"• {p} | 元数据不可读"; } });
        var confirm = MessageBox.Show($"将只发送当前目录直接包含的 {paths.Length} 个文件的元数据（路径、名称、大小、修改时间），不会读取或发送文件内容，也不会递归子目录。\n\n目录：{CurrentDirectory}\n\n{string.Join("\n", info.Take(8))}{(paths.Length > 8 ? "\n…" : "")}\n\n是否继续？", "发送文件信息进行 AI 分析", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        AnalysisStatus.Text = "正在整理文件并请求模型…";
        AnalyzeButtonEnabled(false);
        string? sentPrompt = null;
        try
        {
            var prompt = await Task.Run(() => BuildAnalysisPrompt(paths));
            sentPrompt = prompt;
            var result = await CallModelAsync(prompt);
            AppendAIHistory(new AIRequestRecord(DateTime.Now, config.Provider ?? "OpenAI 兼容 / 自定义", config.Model,
                result.InputTokens, result.OutputTokens, result.TotalTokens, prompt, result.Response));
            var rows = ParseAnalysis(result.Response, paths);
            AnalysisGrid.ItemsSource = rows;
            AnalysisStatus.Text = $"模型返回 {rows.Count} 条建议 · 仅为推测，删除前请自行核实";
        }
        catch (Exception ex)
        {
            if (sentPrompt is not null)
            {
                try { AppendAIHistory(new AIRequestRecord(DateTime.Now, config.Provider ?? "OpenAI 兼容 / 自定义", config.Model, null, null, null, sentPrompt, "请求失败：" + ex.Message)); }
                catch { }
            }
            AnalysisStatus.Text = "分析失败：" + ex.Message;
        }
        finally { AnalyzeButtonEnabled(true); }
    }

    private void AnalyzeButtonEnabled(bool enabled)
    { AnalyzeButton.IsEnabled = enabled; }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private static string BuildAnalysisPrompt(string[] paths)
    {
        var sb = new StringBuilder("你是文件整理助手。输入仅包含当前目录直接包含的文件元数据，不包含文件内容，也不包含子目录内容。对每条 FILE 输入必须输出且只输出一条结果，path 必须逐字复制完整路径；不得遗漏、合并或新增文件。仅根据路径、文件名、扩展名、大小和修改时间，谨慎推测用途及建议。系统文件、应用数据、未知文件默认建议保留或人工判断。只输出 JSON：{\"items\":[{\"path\":\"原始完整路径\",\"kind\":\"文件用途\",\"canDelete\":false,\"reason\":\"理由\",\"confidence\":\"低/中/高\"}]}。删除建议只是推测。\n\n当前目录直属文件元数据：\n");
        foreach (var path in paths)
        {
            try
            {
                var file = new FileInfo(path);
                sb.AppendLine($"FILE | {path} | 名称={file.Name} | 扩展名={file.Extension} | 大小={Human(file.Length)} | 修改时间={file.LastWriteTime:yyyy-MM-dd HH:mm}");
            }
            catch { sb.AppendLine($"FILE | {path} | 元数据不可读"); }
        }
        return sb.ToString();
    }

    private static List<AnalysisRow> ParseAnalysis(string answer, string[] paths)
    {
        var expected = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var json = answer.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal)) { json = json.Trim('`').Trim(); if (json.StartsWith("json", StringComparison.OrdinalIgnoreCase)) json = json[4..].Trim(); }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.GetProperty("items");
            var rows = new Dictionary<string, AnalysisRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.EnumerateArray())
            {
                var path = item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                if (!Path.IsPathFullyQualified(path)) continue;
                path = Path.GetFullPath(path);
                if (!expected.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
                var canDelete = item.TryGetProperty("canDelete", out var d) && d.ValueKind == JsonValueKind.True;
                rows[path] = new AnalysisRow(Path.GetFileName(path), item.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "", canDelete ? "可考虑删除" : "建议保留 / 人工判断", item.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "未提供理由", item.TryGetProperty("confidence", out var c) ? c.GetString() ?? "低" : "低");
            }
            return expected.Select(path => rows.TryGetValue(path, out var row) ? row : new AnalysisRow(Path.GetFileName(path), "未能判断", "需人工判断", "模型未返回该文件的分析结果。", "低")).ToList();
        }
        catch { return expected.Select(path => new AnalysisRow(Path.GetFileName(path), "未能判断", "需人工判断", "模型返回格式无效，未能可靠分析。", "低")).ToList(); }
    }

    private async Task<ModelCallResult> CallModelAsync(string prompt)
    {
        var config = ReadModelConfig() ?? throw new InvalidOperationException("请先保存模型连接配置");
        var key = UnprotectSecret(config.ProtectedKey);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("API 密钥为空");
        var provider = config.Provider ?? "OpenAI 兼容 / 自定义";
        var endpoint = config.Endpoint.TrimEnd('/');
        string body;
        if (provider == "Claude")
        {
            if (!endpoint.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase)) endpoint += "/v1/messages";
            body = JsonSerializer.Serialize(new { model = config.Model, max_tokens = 4096, messages = new[] { new { role = "user", content = prompt } } });
        }
        else if (provider == "Gemini")
        {
            endpoint += $"/models/{Uri.EscapeDataString(config.Model)}:generateContent?key={Uri.EscapeDataString(key)}";
            body = JsonSerializer.Serialize(new { contents = new[] { new { parts = new[] { new { text = prompt } } } }, generationConfig = new { temperature = 0.2 } });
        }
        else
        {
            if (!endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) endpoint += endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? "/chat/completions" : "/v1/chat/completions";
            body = JsonSerializer.Serialize(new { model = config.Model, messages = new[] { new { role = "user", content = prompt } }, temperature = 0.2 });
        }
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (provider == "Claude")
        {
            request.Headers.TryAddWithoutValidation("x-api-key", key);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else if (provider != "Gemini") request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {responseBody[..Math.Min(responseBody.Length, 400)]}");
        using var json = JsonDocument.Parse(responseBody);
        var root = json.RootElement;
        string answer;
        int? inputTokens = null, outputTokens = null, totalTokens = null;
        if (provider == "Claude")
        {
            answer = string.Join("\n", root.GetProperty("content").EnumerateArray().Where(x => x.TryGetProperty("type", out var t) && t.GetString() == "text").Select(x => x.GetProperty("text").GetString()));
            if (root.TryGetProperty("usage", out var usage))
            {
                inputTokens = ReadToken(usage, "input_tokens"); outputTokens = ReadToken(usage, "output_tokens");
                if (inputTokens.HasValue && outputTokens.HasValue) totalTokens = inputTokens + outputTokens;
            }
        }
        else if (provider == "Gemini")
        {
            answer = string.Join("\n", root.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts").EnumerateArray().Where(x => x.TryGetProperty("text", out _)).Select(x => x.GetProperty("text").GetString()));
            if (root.TryGetProperty("usageMetadata", out var usage))
            { inputTokens = ReadToken(usage, "promptTokenCount"); outputTokens = ReadToken(usage, "candidatesTokenCount"); totalTokens = ReadToken(usage, "totalTokenCount"); }
        }
        else
        {
            var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content");
            answer = content.ValueKind == JsonValueKind.String ? content.GetString() ?? "模型未返回文本" : string.Join("\n", content.EnumerateArray().Where(x => x.TryGetProperty("text", out _)).Select(x => x.GetProperty("text").GetString()));
            if (root.TryGetProperty("usage", out var usage))
            { inputTokens = ReadToken(usage, "prompt_tokens"); outputTokens = ReadToken(usage, "completion_tokens"); totalTokens = ReadToken(usage, "total_tokens"); }
        }
        return new ModelCallResult(answer, inputTokens, outputTokens, totalTokens);
    }

    private static int? ReadToken(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var count) ? count : null;
    private string AIHistoryPath => Path.Combine(_dataDir, "ai-requests.jsonl");
    private void AppendAIHistory(AIRequestRecord record)
    {
        Directory.CreateDirectory(_dataDir);
        File.AppendAllText(AIHistoryPath, JsonSerializer.Serialize(record) + Environment.NewLine, Encoding.UTF8);
        LoadAIHistory();
    }

    private string ModelConfigPath => Path.Combine(_dataDir, "model.json");
    private ModelConfig? ReadModelConfig() { try { return File.Exists(ModelConfigPath) ? JsonSerializer.Deserialize<ModelConfig>(File.ReadAllText(ModelConfigPath)) : null; } catch { return null; } }
    private void LoadModelConfig()
    {
        var config = ReadModelConfig(); if (config is null) { ModelStatus.Text = "尚未配置"; return; }
        _loadingModel = true;
        ProviderInput.SelectedItem = ProviderInput.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (x.Content?.ToString() ?? "") == config.Provider) ?? ProviderInput.Items[0];
        EndpointInput.Text = config.Endpoint; ModelInput.Text = config.Model; ApiKeyInput.Clear();
        _loadingModel = false;
        ModelStatus.Text = "已保存连接";
    }

    private static string ProtectSecret(string secret)
    {
        var raw = Encoding.UTF8.GetBytes(secret); var input = Blob.From(raw);
        try { if (!CryptProtectData(ref input, "DiskSentinel", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); try { var bytes = new byte[output.Size]; Marshal.Copy(output.Data, bytes, 0, bytes.Length); return Convert.ToBase64String(bytes); } finally { LocalFree(output.Data); } }
        finally { Marshal.FreeHGlobal(input.Data); }
    }

    private static string UnprotectSecret(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue)) return "";
        var raw = Convert.FromBase64String(protectedValue); var input = Blob.From(raw);
        try { if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); try { var bytes = new byte[output.Size]; Marshal.Copy(output.Data, bytes, 0, bytes.Length); return Encoding.UTF8.GetString(bytes); } finally { LocalFree(output.Data); } }
        finally { Marshal.FreeHGlobal(input.Data); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; public static Blob From(byte[] bytes) { var blob = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) }; Marshal.Copy(bytes, 0, blob.Data, bytes.Length); return blob; } }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    private void UpdateMetrics()
    {
        var now = DateTime.Now;
        ClockValue.Text = now.ToString("ddd HH:mm:ss", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
        UptimeValue.Text = FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64));
        IpValue.Text = GetLocalIPv4();
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var i = ToUInt64(idle); var k = ToUInt64(kernel); var u = ToUInt64(user);
            if (_hasCpuSample)
            {
                var total = (k - _lastKernel) + (u - _lastUser);
                var busy = total > i - _lastIdle ? total - (i - _lastIdle) : 0;
                var percent = total == 0 ? 0 : Math.Clamp(busy * 100d / total, 0, 100);
                CpuValue.Text = $"{percent:0.0}%"; CpuBar.Value = percent;
            }
            _lastIdle = i; _lastKernel = k; _lastUser = u; _hasCpuSample = true;
        }
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory))
        {
            var used = memory.TotalPhysical - memory.AvailablePhysical;
            var percent = memory.TotalPhysical == 0 ? 0 : used * 100d / memory.TotalPhysical;
            MemoryValue.Text = $"{Human((long)used)} / {Human((long)memory.TotalPhysical)}";
            MemoryDetail.Text = $"已用 {percent:0.0}% · 可用 {Human((long)memory.AvailablePhysical)}";
            MemoryBar.Value = percent;
        }
        var (received, sent) = GetNetworkBytes();
        if (_hasNetworkSample)
        {
            var down = received >= _lastReceived ? received - _lastReceived : 0;
            var up = sent >= _lastSent ? sent - _lastSent : 0;
            _sessionReceived += down; _sessionSent += up;
            DownloadValue.Text = $"{HumanRate(down)}"; UploadValue.Text = HumanRate(up);
        }
        _lastReceived = received; _lastSent = sent; _hasNetworkSample = true;
        DownloadTotal.Text = Human((long)_sessionReceived); UploadTotal.Text = Human((long)_sessionSent);
        MetricsUpdated.Text = $"实时指标每秒更新 · 最近更新 {now:HH:mm:ss}";
    }

    private static (ulong Received, ulong Sent) GetNetworkBytes()
    {
        ulong received = 0, sent = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try { var stats = nic.GetIPv4Statistics(); received += (ulong)stats.BytesReceived; sent += (ulong)stats.BytesSent; } catch { }
        }
        return (received, sent);
    }

    private static string GetLocalIPv4()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try
            {
                var address = nic.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal));
                if (address is not null) return address.Address.ToString();
            }
            catch { }
        }
        return "未连接";
    }

    private static string FormatUptime(TimeSpan value) => value.TotalDays >= 1 ? $"{(int)value.TotalDays}天 {value.Hours}时 {value.Minutes}分" : $"{value.Hours}时 {value.Minutes}分";
    private static ulong ToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME value) => ((ulong)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;
    private static string HumanRate(ulong bytes) => $"{(bytes / 1024d):0.0} KB/s";

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME idle, out System.Runtime.InteropServices.ComTypes.FILETIME kernel, out System.Runtime.InteropServices.ComTypes.FILETIME user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] private struct MemoryStatus
    {
        public uint Length, MemoryLoad; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDirectory is null || DriveGrid.SelectedItem is not DriveRow drive) return;
        var parent = Directory.GetParent(CurrentDirectory)?.FullName;
        if (parent is null || !parent.StartsWith(drive.Root, StringComparison.OrdinalIgnoreCase)) parent = drive.Root;
        await ShowDirectory(parent);
    }

    private async void FilesGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesGrid.SelectedItem is FileRow { IsDirectory: true } row) await ShowDirectory(row.FullPath);
    }

    private async Task ShowDirectory(string path)
    {
        CurrentDirectory = path;
        AnalysisGrid.ItemsSource = null;
        AnalysisStatus.Text = "当前目录尚未分析";
        var view = await Task.Run(() => (Rows: TopFiles(_snapshot, _directories, path), Slices: BuildDistribution(path, _snapshot, _directories)));
        if (CurrentDirectory != path) return;
        FilesGrid.ItemsSource = view.Rows;
        ManageFilesGrid.ItemsSource = view.Rows;
        var max = Math.Max(1, view.Rows.Select(x => x.Size).DefaultIfEmpty(0).Max());
        RankingList.ItemsSource = view.Rows.Where(x => x.Size > 0).OrderByDescending(x => x.Size).Take(10)
            .Select((x, i) => new RankingRow($"{i + 1}. {x.Name}", x.Size, Math.Max(2, 210d * x.Size / max))).ToList();
        Breadcrumb.Text = path;
        UpButton.IsEnabled = DriveGrid.SelectedItem is DriveRow d && !string.Equals(path, d.Root, StringComparison.OrdinalIgnoreCase);
        FolderSummary.Text = $"{view.Rows.Count} 个项目";
        ManagePath.Text = path;
        if (DriveGrid.SelectedItem is DriveRow drive) DrawDistribution(path, drive, view.Slices);
    }

    private static List<(string Name, long Size)> BuildDistribution(string path, Dictionary<string, FileState> snapshot, HashSet<string> directories)
    {
        var prefix = path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
        var groups = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in directories.Where(d => string.Equals(ParentPath(d).TrimEnd(Path.DirectorySeparatorChar), path.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))) groups[Path.GetFileName(dir)] = 0;
        foreach (var file in snapshot.Where(f => f.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            var relative = file.Key[prefix.Length..]; var slash = relative.IndexOfAny(['\\', '/']);
            var name = slash < 0 ? Path.GetFileName(file.Key) : relative[..slash];
            groups[name] = groups.GetValueOrDefault(name) + file.Value.Length;
        }
        return groups.Select(x => (x.Key, x.Value)).OrderByDescending(x => x.Value).ToList();
    }

    private void DrawDistribution(string path, DriveRow drive, List<(string Name, long Size)> groups)
    {
        var slices = groups.Take(6).ToList();
        var restSize = groups.Skip(6).Sum(x => x.Size);
        if (restSize > 0) slices.Add(("其他项目", restSize));
        if (slices.Count == 0) slices.Add(("空文件夹", 1));
        var colors = new[] { "#5271F5", "#20A67A", "#F2A93B", "#8B5CF6", "#EF6B73", "#A7B2C3", "#DDE3ED" };
        DistributionChart.Children.Clear();
        var legend = new List<LegendRow>();
        var sum = Math.Max(1, slices.Sum(s => s.Size));
        double angle = -90;
        for (int i = 0; i < slices.Count; i++)
        {
            var (name, size) = slices[i]; var sweep = size / (double)sum * 360;
            AddWedge(DistributionChart, angle, sweep, (Color)ColorConverter.ConvertFromString(colors[i % colors.Length]));
            angle += sweep;
            legend.Add(new LegendRow(name, Human(size), new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i % colors.Length]))));
        }
        LegendList.ItemsSource = legend;
        PieSubtitle.Text = $"{drive.Name} · {path} · 子文件夹和文件占用";
    }

    private async void ManageFiles_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ManageFilesGrid.SelectedItem is FileRow { IsDirectory: true } row) await ShowDirectory(row.FullPath);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = ManageFilesGrid.ItemsSource?.Cast<FileRow>().Where(x => x.IsChecked).ToList() ?? [];
        if (selected.Count == 0 && ManageFilesGrid.SelectedItem is FileRow row) selected.Add(row);
        if (selected.Count == 0) { MessageBox.Show("请勾选或选中要删除的文件/文件夹。", "磁盘管理", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var folderCount = selected.Count(x => x.IsDirectory);
        var detail = folderCount > 0 ? $"其中 {folderCount} 个文件夹会连同内容一起移入回收站。" : "所选文件会移入回收站。";
        var list = string.Join("\n", selected.Take(10).Select(x => x.FullPath)) + (selected.Count > 10 ? $"\n…另有 {selected.Count - 10} 项" : "");
        if (MessageBox.Show($"确定将 {selected.Count} 项移入回收站吗？\n\n{list}\n\n{detail}", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var errors = new List<string>();
            var deleted = new List<FileRow>();
            foreach (var item in selected)
            {
                try
                {
                    if (item.IsDirectory) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(item.FullPath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.FullPath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    deleted.Add(item);
                }
                catch (Exception ex) { errors.Add($"{item.Name}: {ex.Message}"); }
            }
            if (deleted.Count == 0) { AnalysisStatus.Text = $"删除失败：{string.Join("；", errors.Take(3))}"; return; }
            foreach (var item in deleted)
            {
                var fullPath = Path.GetFullPath(item.FullPath);
                if (item.IsDirectory)
                {
                    var prefix = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    foreach (var key in _snapshot.Keys.Where(k => k.Equals(fullPath, StringComparison.OrdinalIgnoreCase) || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray()) _snapshot.Remove(key);
                    _directories.RemoveWhere(d => d.Equals(fullPath, StringComparison.OrdinalIgnoreCase) || d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                }
                else _snapshot.Remove(fullPath);
            }
            var removedNames = deleted.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (AnalysisGrid.ItemsSource is IEnumerable<AnalysisRow> analysisRows)
                AnalysisGrid.ItemsSource = analysisRows.Where(x => !removedNames.Contains(x.Name)).ToList();
            AnalysisStatus.Text = errors.Count == 0 ? $"已将 {deleted.Count} 项移入回收站" : $"已移入回收站 {deleted.Count} 项；失败：{string.Join("；", errors.Take(3))}";
            if (CurrentDirectory is not null) await ShowDirectory(CurrentDirectory);
            await ScanAsync();
            if (CurrentDirectory is not null) await ShowDirectory(CurrentDirectory);
        }
        catch (Exception ex) { MessageBox.Show($"删除失败：{ex.Message}", "磁盘管理", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static void AddWedge(Canvas canvas, double start, double sweep, Color color)
    {
        const double center = 95, radius = 88;
        if (sweep >= 359.9) { canvas.Children.Add(new Ellipse { Width = radius * 2, Height = radius * 2, Fill = new SolidColorBrush(color) }); return; }
        var a1 = start * Math.PI / 180; var a2 = (start + sweep) * Math.PI / 180;
        var p1 = new Point(center + radius * Math.Cos(a1), center + radius * Math.Sin(a1));
        var p2 = new Point(center + radius * Math.Cos(a2), center + radius * Math.Sin(a2));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(center, center), true, true);
            ctx.LineTo(p1, true, false);
            if (sweep > 0.1) ctx.ArcTo(p2, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze(); canvas.Children.Add(new System.Windows.Shapes.Path { Data = geo, Fill = new SolidColorBrush(color), Stroke = Brushes.White, StrokeThickness = 1 });
    }

    private static string Human(long bytes)
    { string[] u = ["B", "KB", "MB", "GB", "TB"]; double n = bytes; var i = 0; while (n >= 1024 && i < u.Length - 1) { n /= 1024; i++; } return $"{n:0.##} {u[i]}"; }

    private sealed record ScanResult(List<DriveRow> Drives, Dictionary<string, FileState> Files, HashSet<string> Directories);
    private sealed record FileState(long Length, DateTime Modified);
    private sealed record ChangeRow(string Path, long Delta, string Kind, string Cause, DateTime Time);
    private sealed class DriveRow(string name, string format, long used, long total, long free)
    {
        public string Name { get; } = name; public string Root { get; } = name; public string Format { get; } = format;
        public long Used { get; } = used; public long Total { get; } = total; public long Free { get; } = free;
        public string UsageText { get; } = $"{Human(used)} / {Human(total)}"; public string FreeText { get; } = Human(free);
        public string PercentText { get; } = $"{(total == 0 ? 0 : used * 100d / total):0}%";
        public double BarWidth { get; } = total == 0 ? 0 : Math.Clamp(used * 100d / total, 0, 100) * 1.1;
    }
    private sealed record FileRow(string Name, string Type, long Size, DateTime Modified, bool IsDirectory, string FullPath)
    { public bool IsChecked { get; set; } public string SizeText => Human(Size); public string ModifiedText => Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm"); }
    private sealed record LogDisplay(string TimeText, string DeltaText, string Kind, string Cause, string Path);
    private sealed record AIRequestRecord(DateTime Time, string Provider, string Model, int? InputTokens, int? OutputTokens, int? TotalTokens, string Prompt, string Response);
    private sealed record AIHistoryDisplay(string TimeText, string Provider, string Model, string InputTokens, string OutputTokens, string TotalTokens, string Prompt, string Response);
    private sealed record ModelCallResult(string Response, int? InputTokens, int? OutputTokens, int? TotalTokens);
    private sealed record LegendRow(string Name, string SizeText, Brush Color);
    private sealed record ModelConfig(string Endpoint, string Model, string ProtectedKey, string? Provider = "OpenAI 兼容 / 自定义");
    private sealed record AnalysisRow(string Name, string Kind, string Recommendation, string Reason, string Confidence);
    private sealed record RankingRow(string RankName, long Size, double BarWidth) { public string SizeText => Human(Size); }
}
