using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Win32;
using PixivRenamer.Core;

namespace PixivRenamer;

public partial class MainWindow : Window
{
    private readonly string _baseDir = AppConfig.ExeDirectory();
    private string ConfigPath => Path.Combine(_baseDir, "PixivRenamer.json");
    private AppConfig _config = new();
    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _closePending;
    private long _runCount;
    private bool _updateChecking;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"PixivRenamer {AppConfig.VersionText}  ·  pixiv 插画批量重命名工具";
        if (SidebarVersionText != null) SidebarVersionText.Text = $"v{AppConfig.VersionText} · WPF";
        LoadConfigFromDisk();
        DryRunCheckBox_Changed(this, new RoutedEventArgs());
        UpdateElevationUi();
        AppendLog($"PixivRenamer {AppConfig.VersionText} 已启动。建议先点击「测试网络」确认 pixiv 可用。");
        Loaded += MainWindow_Loaded;
    }

    // ---------------- 启动时静默检查更新 ----------------
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_config.CheckUpdateOnStartup) return;
        await Task.Delay(2500);   // 等界面先渲染出来，别和启动抢资源
        if (_updateChecking) return;
        try
        {
            var result = await CheckForUpdateAsync(silent: true);
            if (result is { Ok: true, HasUpdate: true }) ShowUpdateDialog(result);
        }
        catch (Exception ex)
        {
            AppendLog("启动检查更新失败：" + ex.Message);
        }
    }

    /// <summary>查询 GitHub 上的最新版本；silent=true 时把"已是最新/失败"只写日志不弹窗。</summary>
    private async Task<UpdateCheckResult?> CheckForUpdateAsync(bool silent)
    {
        if (_updateChecking) return null;
        _updateChecking = true;
        try
        {
            var cfg = CollectConfig();
            // 让更新检查也走解析后的代理，否则用代理的机器查不到 GitHub
            var resolution = await Task.Run(() => ProxyResolver.Resolve(cfg, null));
            cfg.Proxy = resolution.Chosen;
            cfg.UseSystemProxy = resolution.Chosen.Length == 0 && cfg.ProxyMode != "direct";

            AppendLog($"正在检查更新（GitHub：{AppInfo.RepositoryOwner}/{AppInfo.RepositoryName}）…");
            var result = await new UpdateService().CheckAsync(cfg);
            _config.LastUpdateCheck = TextUtils.NowStamp();

            if (!result.Ok)
            {
                AppendLog("检查更新失败：" + result.Error);
                if (!silent)
                {
                    var path = NetworkDiagnostics.WriteReport(_baseDir, "检查更新失败", cfg, resolution, null, null,
                        "检查更新失败的原始信息：" + result.Error);
                    MessageBox.Show(this,
                        "检查更新失败：\n" + result.Error +
                        "\n\n可能是网络问题（需要能访问 github.com）。\n" +
                        (path.Length > 0 ? "诊断报告：" + path : ""),
                        "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return result;
            }

            AppendLog(result.Describe());
            if (result.HasUpdate)
            {
                AppendLog($"新版本 {result.LatestVersion} 发布信息：{result.ReleaseUrl}");
            }
            else if (!silent)
            {
                MessageBox.Show(this,
                    $"已是最新版本（当前 {result.CurrentVersion}）。\n\n项目主页：{AppInfo.RepositoryUrl}",
                    "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return result;
        }
        finally { _updateChecking = false; }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        => await CheckForUpdateAsync(silent: false);

    private void OpenProject_Click(object sender, RoutedEventArgs e) => OpenUrl(AppInfo.RepositoryUrl);

    private void OpenIssues_Click(object sender, RoutedEventArgs e)
    {
        AppendLog("打开反馈页面：" + AppInfo.IssuesUrl);
        OpenUrl(AppInfo.IssuesUrl);
    }

    /// <summary>发现新版本时的提示窗口。</summary>
    private void ShowUpdateDialog(UpdateCheckResult r)
    {
        var notes = string.IsNullOrWhiteSpace(r.Notes) ? "（本次发布没有填写更新说明）" : r.Notes.Trim();
        if (notes.Length > 4000) notes = notes[..4000] + "…";

        var dialog = new Window
        {
            Title = "发现新版本",
            Owner = this,
            Width = 680,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("App.Surface"),
        };
        var grid = new Grid { Margin = new Thickness(18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = $"发现新版本  {r.LatestVersion}",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("App.Accent"),
        });
        header.Children.Add(new TextBlock
        {
            Text = $"当前版本 {r.CurrentVersion}" +
                   (r.PublishedAt.Length > 0 ? $"　｜　发布于 {r.PublishedAt}" : "") +
                   (r.IsPrerelease ? "　｜　预先发布版" : ""),
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("App.TextMuted"),
        });
        grid.Children.Add(header);

        var box = new TextBox
        {
            Text = notes,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 14, 0, 0),
            FontSize = 13,
        };
        box.Style = (Style)FindResource("LogBox");
        box.Height = double.NaN;
        box.MinHeight = 240;
        Grid.SetRow(box, 1);
        grid.Children.Add(box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        if (!string.IsNullOrWhiteSpace(r.DownloadUrl))
        {
            var download = new Button
            {
                Content = "下载新版本",
                Width = 116,
                Margin = new Thickness(0, 0, 8, 0),
                Style = (Style)FindResource("PrimaryButton"),
            };
            download.Click += (_, _) => { OpenUrl(r.DownloadUrl); dialog.Close(); };
            buttons.Children.Add(download);
        }
        var openPage = new Button
        {
            Content = "打开发布页",
            Width = 110,
            Margin = new Thickness(0, 0, 8, 0),
            Style = string.IsNullOrWhiteSpace(r.DownloadUrl) ? (Style)FindResource("PrimaryButton") : (Style)FindResource("FlatButton"),
        };
        openPage.Click += (_, _) => { OpenUrl(r.ReleaseUrl); dialog.Close(); };
        buttons.Children.Add(openPage);

        var later = new Button { Content = "以后再说", Width = 96, IsCancel = true };
        later.Click += (_, _) => dialog.Close();
        buttons.Children.Add(later);

        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);
        dialog.Content = grid;
        AppendLog($"提示：新版本 {r.LatestVersion} 可用，发布页 {r.ReleaseUrl}");
        dialog.ShowDialog();
    }

    // ---------------- 管理员权限（提权） ----------------
    /// <summary>同步权限相关界面：状态文字、开关可用性、被用户拒绝提权时的提示。</summary>
    private void UpdateElevationUi()
    {
        var elevated = Elevation.IsElevated;
        if (ElevatedStateText != null)
        {
            ElevatedStateText.Text = elevated ? "当前：管理员" : "当前：普通用户";
            ElevatedStateText.Foreground = elevated
                ? (System.Windows.Media.Brush)FindResource("App.Success")
                : (System.Windows.Media.Brush)FindResource("App.Warning");
        }
        if (ElevateNowButton != null)
        {
            ElevateNowButton.IsEnabled = !elevated;
            ElevateNowButton.Content = elevated ? "已是管理员" : "以管理员重启";
        }
        if (RunAsAdminCheckBox != null)
            RunAsAdminCheckBox.IsEnabled = CanWriteConfig();
        if (CheckUpdateOnStartupCheckBox != null)
            CheckUpdateOnStartupCheckBox.IsEnabled = CanWriteConfig();

        if (App.ElevationDeclined && !elevated)
        {
            AppendLog("你取消了管理员提权，本次以普通权限运行。");
            AppendLog("提示：如果浏览器是「以管理员身份运行」的，普通权限下可能无法与它交互；");
            AppendLog("      可以勾选「默认以管理员启动」后重启程序，或点「以管理员重启」。");
        }
    }

    /// <summary>exe 目录是否可写：Program Files 之类的位置需要提权才能保存配置。</summary>
    private bool CanWriteConfig()
    {
        try
        {
            var probe = Path.Combine(_baseDir, ".write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private void ElevateNow_Click(object sender, RoutedEventArgs e)
    {
        if (Elevation.IsElevated)
        {
            MessageBox.Show(this, "当前已经是管理员权限。", "PixivRenamer",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        AppendLog("正在请求以管理员身份重启程序（会弹出 UAC 确认框）");
        if (Elevation.TryRelaunchElevated())
        {
            _running = false;
            if (_cts != null) { try { _cts.Cancel(); } catch { } }
            Close();
            return;
        }
        AppendLog("提权被取消或失败，继续以普通权限运行。");
        MessageBox.Show(this,
            "未能以管理员身份重启（可能你在 UAC 提示里点了「否」，或系统策略禁止提权）。\n\n" +
            "如果确实需要管理员权限（比如要与之交互的浏览器是以管理员身份运行的），" +
            "可以右键 PixivRenamer.exe -> 以管理员身份运行。",
            "PixivRenamer", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void RunAsAdminCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (RunAsAdminCheckBox == null) return;
        AppendLog(RunAsAdminCheckBox.IsChecked == true
            ? "已设置：下次启动自动以管理员身份运行（会弹 UAC 确认框）。"
            : "已设置：下次启动使用普通权限。");
    }

    // ---------------- 配置 ----------------
    /// <summary>切换页面（0 整理 / 1 pixiv·识图 / 2 任务与日志），界面自检与截图用。</summary>
    public void SelectPage(int index)
    {
        if (NavList == null) return;
        NavList.SelectedIndex = Math.Clamp(index, 0, 2);
        if (MainTabs != null) MainTabs.SelectedIndex = NavList.SelectedIndex;
    }

    private void LoadConfigFromDisk()
    {
        try
        {
            _config = AppConfig.Load(ConfigPath);
            _config.Finalize(_baseDir);
            PopulateControls();
            AppendLog("已加载配置：" + ConfigPath);
        }
        catch (Exception ex)
        {
            AppendLog("加载配置失败：" + ex.Message);
        }
    }

    private void PopulateControls()
    {
        SourceTextBox.Text = _config.SourceDir;
        OutputTextBox.Text = _config.OutputDir;
        CopyCheckBox.IsChecked = _config.CopyMode;
        OverwriteCheckBox.IsChecked = _config.Overwrite;
        RecursiveCheckBox.IsChecked = _config.Recursive;
        HiddenCheckBox.IsChecked = _config.IncludeHidden;
        DryRunCheckBox.IsChecked = _config.DryRun;
        ResumeCheckBox.IsChecked = _config.Resume;
        SplitR18CheckBox.IsChecked = _config.SplitR18;
        TemplateTextBox.Text = _config.Template;
        UnknownAuthorTextBox.Text = _config.UnknownAuthor;
        UnknownTitleTextBox.Text = _config.UnknownTitle;
        MoveUnresolvedCheckBox.IsChecked = _config.MoveUnresolvedToFolder;
        UnresolvedFolderTextBox.Text = _config.UnresolvedFolderName;
        RunAsAdminCheckBox.IsChecked = _config.RunAsAdmin;
        CheckUpdateOnStartupCheckBox.IsChecked = _config.CheckUpdateOnStartup;

        // 旧配置里可能保存了失效的绝对报告路径，这里自动清掉，改用源目录默认报告
        var reportPath = _config.ReportPath;
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            var reportDir = Path.GetDirectoryName(reportPath);
            if (string.IsNullOrWhiteSpace(reportDir) || !Directory.Exists(reportDir))
                reportPath = "";
        }
        ReportTextBox.Text = reportPath;
        LogToFileCheckBox.IsChecked = _config.LogToFile;
        VerboseCheckBox.IsChecked = _config.Verbose;

        OrganizeComboBox.SelectedIndex = _config.Organize switch
        {
            "none" => 0, "author" => 1, "date" => 2, "author_date" => 3, "date_author" => 4, _ => 1,
        };

        CookieTextBox.Text = _config.Cookie;
        ProxyTextBox.Text = _config.Proxy;
        ProxyModeComboBox.SelectedIndex = _config.ProxyMode switch
        {
            "explicit" => 1, "system" => 2, "direct" => 3, _ => 0,
        };
        ProxyMode_Changed(this, new RoutedEventArgs());
        ThreadsTextBox.Text = _config.Threads.ToString();
        DelayTextBox.Text = _config.PixivDelayMs.ToString();
        TimeoutTextBox.Text = _config.TimeoutMs.ToString();
        RetriesTextBox.Text = _config.Retries.ToString();
        HtmlFallbackCheckBox.IsChecked = _config.HtmlFallback;
        CacheCheckBox.IsChecked = _config.CacheEnabled;
        CacheDaysTextBox.Text = _config.CacheDays.ToString();
        ReverseCheckBox.IsChecked = _config.ReverseEnabled;
        OrderTextBox.Text = _config.ReverseOrder;
        SauceKeyTextBox.Text = _config.SauceNaoApiKey;
        MinSimTextBox.Text = _config.MinSimilarity.ToString("0");
        MaxKbTextBox.Text = _config.MaxUploadKB.ToString();
        VerifyByHashCheckBox.IsChecked = _config.VerifyByHash;
        HashDistanceTextBox.Text = _config.HashMaxDistance.ToString();
        GroupByHashCheckBox.IsChecked = _config.GroupByPerceptualHash;
        ReverseWhenPixivDownCheckBox.IsChecked = _config.ReverseWhenPixivDown;
        ReverseQueueCheckBox.IsChecked = _config.ReverseQueueEnabled;
        ReverseConcurrencyTextBox.Text = _config.ReverseConcurrency.ToString();
        ReverseCooldownTextBox.Text = _config.ReverseCooldownMs.ToString();
        ReverseRetriesTextBox.Text = _config.ReverseMaxRetries.ToString();
        ReverseTimeoutTextBox.Text = _config.ReverseTimeoutMs.ToString();
    }

    private AppConfig CollectConfig(bool finalize = true)
    {
        var cfg = new AppConfig
        {
            SourceDir = SourceTextBox.Text.Trim(),
            OutputDir = OutputTextBox.Text.Trim(),
            CopyMode = CopyCheckBox.IsChecked == true,
            Overwrite = OverwriteCheckBox.IsChecked == true,
            Recursive = RecursiveCheckBox.IsChecked == true,
            IncludeHidden = HiddenCheckBox.IsChecked == true,
            DryRun = DryRunCheckBox.IsChecked == true,
            Resume = ResumeCheckBox.IsChecked == true,
            SplitR18 = SplitR18CheckBox.IsChecked == true,
            Template = TemplateTextBox.Text.Trim(),
            UnknownAuthor = UnknownAuthorTextBox.Text.Trim(),
            UnknownTitle = UnknownTitleTextBox.Text.Trim(),
            MoveUnresolvedToFolder = MoveUnresolvedCheckBox.IsChecked == true,
            UnresolvedFolderName = UnresolvedFolderTextBox.Text.Trim(),
            RunAsAdmin = RunAsAdminCheckBox.IsChecked == true,
            CheckUpdateOnStartup = CheckUpdateOnStartupCheckBox.IsChecked == true,
            LastUpdateCheck = _config.LastUpdateCheck,
            ReportPath = ReportTextBox.Text.Trim(),
            LogToFile = LogToFileCheckBox.IsChecked == true,
            Verbose = VerboseCheckBox.IsChecked == true,
            Cookie = CookieTextBox.Text.Trim(),
            Proxy = ProxyTextBox.Text.Trim(),
            ProxyMode = SelectedProxyMode(),
            UseSystemProxy = SelectedProxyMode() == "system",
            Threads = ParseInt(ThreadsTextBox.Text, 3),
            PixivDelayMs = ParseInt(DelayTextBox.Text, 1200),
            TimeoutMs = ParseInt(TimeoutTextBox.Text, 30000),
            Retries = ParseInt(RetriesTextBox.Text, 3),
            HtmlFallback = HtmlFallbackCheckBox.IsChecked == true,
            CacheEnabled = CacheCheckBox.IsChecked == true,
            CacheDays = ParseInt(CacheDaysTextBox.Text, 30),
            ReverseEnabled = ReverseCheckBox.IsChecked == true,
            ReverseOrder = OrderTextBox.Text.Trim(),
            SauceNaoApiKey = SauceKeyTextBox.Text.Trim(),
            MinSimilarity = ParseDouble(MinSimTextBox.Text, 60),
            MaxUploadKB = ParseInt(MaxKbTextBox.Text, 2048),
            VerifyByHash = VerifyByHashCheckBox.IsChecked == true,
            HashMaxDistance = ParseInt(HashDistanceTextBox.Text, 10),
            GroupByPerceptualHash = GroupByHashCheckBox.IsChecked == true,
            ReverseWhenPixivDown = ReverseWhenPixivDownCheckBox.IsChecked == true,
            ReverseQueueEnabled = ReverseQueueCheckBox.IsChecked == true,
            ReverseConcurrency = ParseInt(ReverseConcurrencyTextBox.Text, 1),
            ReverseCooldownMs = ParseInt(ReverseCooldownTextBox.Text, 12000),
            ReverseMaxRetries = ParseInt(ReverseRetriesTextBox.Text, 2),
            ReverseTimeoutMs = ParseInt(ReverseTimeoutTextBox.Text, 90000),
            Organize = OrganizeComboBox.SelectedIndex switch
            {
                0 => "none", 1 => "author", 2 => "date", 3 => "author_date", 4 => "date_author", _ => "author",
            },
        };
        if (finalize) cfg.Finalize(_baseDir);
        return cfg;
    }

    private static int ParseInt(string text, int def) => int.TryParse(text.Trim(), out var v) ? v : def;
    private static double ParseDouble(string text, double def) => double.TryParse(text.Trim(), out var v) ? v : def;

    // ---------------- 浏览 / 拖拽 ----------------
    private void BrowseSource_Click(object sender, RoutedEventArgs e) => PickFolder(SourceTextBox);
    private void BrowseOutput_Click(object sender, RoutedEventArgs e) => PickFolder(OutputTextBox);

    private void PickFolder(TextBox target)
    {
        try
        {
            var dialog = new OpenFolderDialog { Title = "选择文件夹" };
            if (Directory.Exists(target.Text)) dialog.InitialDirectory = target.Text;
            if (dialog.ShowDialog(this) == true)
            {
                target.Text = dialog.FolderName;
                AppendLog("已选择文件夹：" + dialog.FolderName);
            }
        }
        catch (Exception ex)
        {
            AppendLog("打开文件夹选择器失败：" + ex.Message);
            MessageBox.Show(this, "打开文件夹选择器失败：\n" + ex.Message + "\n\n可以直接在路径框中粘贴文件夹路径。",
                "PixivRenamer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files == null || files.Length == 0) return;
        var path = files[0];
        if (Directory.Exists(path)) SourceTextBox.Text = path;
        else if (File.Exists(path)) SourceTextBox.Text = Path.GetDirectoryName(path) ?? "";
        AppendLog("已拖入：" + SourceTextBox.Text);
        e.Handled = true;
    }

    // ---------------- 打开目录 / 链接（带兜底） ----------------
    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        AppendLog("打开链接：" + e.Uri.AbsoluteUri);
        OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private static bool TryOpen(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    private void OpenWithCmd(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c start \"\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            AppendLog("打开失败：" + target + "：" + ex.Message);
            MessageBox.Show(this, "无法打开：\n" + target + "\n\n" + ex.Message,
                "PixivRenamer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenWithShell(string path)
    {
        if (File.Exists(path)) { if (TryOpen(path)) return; OpenWithCmd(path); }
        else if (Directory.Exists(path)) OpenFolder(path);
        else OpenWithCmd(path);
    }

    private void OpenFolder(string path)
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        try
        {
            Process.Start(new ProcessStartInfo(explorer, $"\"{path}\"") { UseShellExecute = true });
            return;
        }
        catch (Exception ex)
        {
            AppendLog("explorer 打开失败：" + ex.Message);
        }
        OpenWithCmd(path);
    }

    private void OpenUrl(string url)
    {
        if (TryOpen(url)) return;
        OpenWithCmd(url);
    }

    private void OpenReportButton_Click(object sender, RoutedEventArgs e)
    {
        var cfg = CollectConfig();
        string dir;
        if (!string.IsNullOrWhiteSpace(cfg.ReportPath))
            dir = Path.GetDirectoryName(cfg.ReportPath) ?? _baseDir;
        else if (!string.IsNullOrWhiteSpace(cfg.SourceDir) && Directory.Exists(cfg.SourceDir))
            dir = cfg.SourceDir;
        else
            dir = _baseDir;

        if (string.IsNullOrWhiteSpace(dir)) dir = _baseDir;
        if (!Directory.Exists(dir))
        {
            AppendLog("报告目录不存在：" + dir);
            MessageBox.Show(this, "报告目录不存在：\n" + dir +
                "\n\n如果还没有生成报告，请先点击开始整理/开始预览。",
                "PixivRenamer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        AppendLog("正在打开报告目录：" + dir);
        OpenFolder(dir);
    }

    // ---------------- 未识别归档目录 ----------------
    private string UnresolvedDir()
    {
        var cfg = CollectConfig();
        var root = !string.IsNullOrWhiteSpace(cfg.OutputDir) ? cfg.OutputDir : cfg.SourceDir;
        var name = string.IsNullOrWhiteSpace(cfg.UnresolvedFolderName) ? "未识别" : cfg.UnresolvedFolderName;
        if (string.IsNullOrWhiteSpace(root)) return "";
        return Path.Combine(root, name);
    }

    private void OpenUnresolved_Click(object sender, RoutedEventArgs e)
    {
        var dir = UnresolvedDir();
        if (string.IsNullOrWhiteSpace(dir))
        {
            MessageBox.Show(this, "请先选择源文件夹（或输出目录）。", "PixivRenamer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!Directory.Exists(dir))
        {
            AppendLog("未识别文件夹还不存在：" + dir);
            MessageBox.Show(this,
                "未识别文件夹还不存在：\n" + dir +
                "\n\n它会在正式执行整理后自动创建，并收纳无法识别作者/标题的图片。\n" +
                "（预览模式不会创建文件夹，也不改动任何文件。）",
                "PixivRenamer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        AppendLog("正在打开未识别文件夹：" + dir);
        OpenFolder(dir);
    }

    private void OpenUnresolvedList_Click(object sender, RoutedEventArgs e)
    {
        var cfg = CollectConfig();
        var dir = string.IsNullOrWhiteSpace(cfg.ReportPath)
            ? cfg.SourceDir
            : (Path.GetDirectoryName(cfg.ReportPath) ?? cfg.SourceDir);
        var path = string.IsNullOrWhiteSpace(dir) ? "" : Path.Combine(dir, "_unresolved.txt");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show(this,
                "还没有生成未识别列表（_unresolved.txt）。\n\n请先执行一次整理或预览。",
                "PixivRenamer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        AppendLog("正在打开未识别列表：" + path);
        OpenWithShell(path);
    }

    // ---------------- 网络 / 代理 / 自动 Cookie ----------------
    /// <summary>当前界面选择的代理模式。</summary>
    private string SelectedProxyMode() => ProxyModeComboBox?.SelectedIndex switch
    {
        1 => "explicit",
        2 => "system",
        3 => "direct",
        _ => "auto",
    };

    private void ProxyMode_Changed(object sender, RoutedEventArgs e)
    {
        if (ProxyTextBox == null || ProxyModeComboBox == null) return;
        var mode = SelectedProxyMode();
        ProxyTextBox.IsEnabled = mode == "explicit";
        if (ProxyHintText != null)
        {
            ProxyHintText.Text = mode switch
            {
                "explicit" => "手动指定：请填写本机代理端口，Clash Verge 默认 127.0.0.1:7897，Clash for Windows 默认 127.0.0.1:7890。填错端口是最常见的失败原因，可点「检测本地代理」自动找。",
                "system" => "使用系统代理：读取 Windows「Internet 选项」里的代理设置（Clash 打开「系统代理」开关后才有效）。",
                "direct" => "直连：不使用任何代理。只有在能直接访问 pixiv 的网络下才这么选。",
                _ => "自动侦测：依次尝试「配置文件里读到的 Clash 端口 → 系统代理设置 → 常见本地端口」，并逐个验证哪个真能用，找到就用它。便携版发给别人时推荐保持这个模式。",
            };
        }
    }

    /// <summary>「检测本地代理」：扫描并验证所有候选端口，把结果写给用户看。</summary>
    private async void DetectProxy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppendLog("正在检测本地代理（Clash / v2rayN / Sing-box 等监听的端口）…");
            var cfg = CollectConfig();
            var manual = AppConfig.NormalizeProxy(cfg.Proxy);
            var text = await Task.Run(() =>
            {
                var lines = new List<string>();
                lines.Add("本机代理端口检测结果");
                lines.Add(new string('─', 46));
                lines.Add($"Clash 配置里读到的端口：{DescribeClashPorts()}");
                lines.Add($"系统代理设置：{(ProxyResolver.SystemProxy() is { Length: > 0 } sp ? sp : "（未设置）")}");
                lines.Add("");
                lines.Add("逐个验证（能不能真正作为 HTTP 代理转发）：");

                var found = new List<string>();
                var candidates = new List<ProxyCandidate>();
                if (manual.Length > 0)
                    candidates.Add(new ProxyCandidate { Uri = manual, Source = "你填写的地址" });
                candidates.AddRange(ProxyResolver.Collect());

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in candidates)
                {
                    if (!seen.Add(c.Uri)) continue;
                    c.Listening = ProxyResolver.IsListening(c.Uri);
                    if (c.Listening)
                    {
                        var (ok, detail) = ProxyResolver.Verify(c.Uri);
                        c.HttpProxyOk = ok;
                        c.Detail = detail;
                        if (ok) found.Add(c.Uri);
                    }
                    else c.Detail = "端口未监听";
                    lines.Add($"  {(c.HttpProxyOk ? "✔ 可用" : c.Listening ? "△ 端口通但代理不可用" : "✘ 未监听"),-22} {c.Uri}   [{c.Source}]");
                }
                lines.Add("");
                if (found.Count > 0)
                {
                    lines.Add($"结论：可用代理 {found.Count} 个，首选 {found[0]}");
                    lines.Add("如果自动侦测没能选中它，就把代理模式改成「手动指定」并填这个地址。");
                }
                else
                {
                    lines.Add("结论：没有检测到可用代理。");
                    lines.Add("请确认代理软件已启动、已选好节点，并且在浏览器里能打开 pixiv。");
                }
                return (Text: string.Join("\r\n", lines), Found: found);
            });

            foreach (var line in text.Text.Split("\r\n")) AppendLog(line);

            var report = NetworkDiagnostics.WriteReport(_baseDir, "用户手动点击「检测本地代理」",
                cfg, null, null, null, text.Text);
            if (report.Length > 0) AppendLog("检测报告：" + report);

            var dialog = new Window
            {
                Title = "本地代理检测结果",
                Owner = this,
                Width = 720,
                Height = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (System.Windows.Media.Brush)FindResource("App.Surface"),
            };
            var grid = new Grid { Margin = new Thickness(18) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var box = new TextBox
            {
                Text = text.Text + (report.Length > 0 ? "\r\n\r\n诊断报告：" + report : ""),
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
                FontSize = 12.5,
            };
            box.Style = (Style)FindResource("LogBox");
            box.Height = double.NaN;
            box.MinHeight = 300;
            grid.Children.Add(box);
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            Grid.SetRow(buttons, 1);
            var copy = new Button { Content = "复制结果", Width = 100, Margin = new Thickness(0, 0, 8, 0) };
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(text.Text); AppendLog("检测结果已复制到剪贴板"); } catch { }
            };
            var openBtn = new Button { Content = "打开诊断目录", Width = 120, Margin = new Thickness(0, 0, 8, 0) };
            openBtn.Click += (_, _) => { try { System.Diagnostics.Process.Start("explorer.exe", NetworkDiagnostics.ReportDir(_baseDir)); } catch { } };
            var close = new Button { Content = "关闭", Width = 90, IsCancel = true, Style = (Style)FindResource("PrimaryButton") };
            close.Click += (_, _) => dialog.Close();
            buttons.Children.Add(copy);
            buttons.Children.Add(openBtn);
            buttons.Children.Add(close);
            grid.Children.Add(buttons);
            dialog.Content = grid;
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            AppendLog("检测本地代理失败：" + ex.Message);
        }
    }

    /// <summary>把读到的 Clash 端口整理成一行文字（诊断用）。</summary>
    private static string DescribeClashPorts()
    {
        var parts = ProxyResolver.Collect(includeCommonPorts: false)
            .Where(c => c.Source.Contains("配置"))
            .Select(c => c.Uri.Replace("http://", "") + "（" + c.Source + "）")
            .Distinct()
            .ToList();
        return parts.Count > 0 ? string.Join("，", parts) : "未读到（可能没装 Clash，或配置目录不同）";
    }

    private async Task<(bool Ok, string Reason)> ProbePixivAsync(ProxyResolution? resolved = null)
    {
        var cfg = CollectConfig();
        // 先解析代理（auto 模式会自动侦测本机 Clash / v2rayN 端口），否则探测必然失败
        var resolution = resolved ?? await Task.Run(() => ProxyResolver.Resolve(cfg, AppendLog));
        var probe = cfg.Clone();
        probe.Proxy = resolution.Chosen;
        probe.UseSystemProxy = resolution.Chosen.Length == 0 && cfg.ProxyMode != "direct";
        probe.Retries = 1;
        probe.TimeoutMs = Math.Min(cfg.TimeoutMs, 8000);
        using var http = new HttpService(probe, msg => AppendLog(msg));
        http.SetInterval("www.pixiv.net", 0);
        var resp = await http.GetAsync(cfg.PixivApiBase.TrimEnd('/') + "/", "www.pixiv.net", null,
                                       CancellationToken.None, 1);
        if (resp.Ok) return (true, $"已连通，代理：{resolution.DescribeChosen()}");
        var reason = resp.TransportOk ? $"HTTP {resp.Status}" : resp.Error;
        return (false, $"{reason}（代理：{resolution.DescribeChosen()}）");
    }

    private async void TestNetwork_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppendLog("=== 正在测试网络（含代理侦测）===");
            var cfg = CollectConfig();
            var resolution = await Task.Run(() => ProxyResolver.Resolve(cfg, null));
            AppendLog("代理：" + resolution.DescribeChosen());
            if (resolution.InvalidInput.Length > 0) AppendLog("⚠ " + resolution.InvalidReason);
            var pixivOk = await ProbePixivAsync(resolution);

            var diagCfg = cfg.Clone();
            diagCfg.Proxy = resolution.Chosen;
            diagCfg.UseSystemProxy = resolution.Chosen.Length == 0 && cfg.ProxyMode != "direct";
            diagCfg.TimeoutMs = Math.Min(cfg.TimeoutMs, 12000);
            var probes = await NetworkDiagnostics.ProbeAsync(diagCfg, cfg.PixivApiBase, CancellationToken.None);

            var pixivProbe = probes.FirstOrDefault(p => p.Name == "pixiv");
            var detail = new System.Text.StringBuilder();
            detail.AppendLine(ProxyResolver.Describe(resolution));
            detail.AppendLine();
            detail.AppendLine("连通性测试：");
            foreach (var p in probes)
                detail.AppendLine($"  {(p.Ok ? "✔" : "✘")} {p.Name}：{p.Detail}");
            detail.AppendLine();
            detail.AppendLine($"pixiv 预检：{(pixivOk.Ok ? "成功" : "失败 —— " + pixivOk.Reason)}");
            foreach (var line in detail.ToString().Split("\r\n")) AppendLog(line);

            if (pixivProbe is { Ok: true } && pixivOk.Ok)
            {
                var msg = $"网络测试成功：可以访问 pixiv。\r\n\r\n{ProxyResolver.Describe(resolution)}";
                AppendLog("网络测试成功。");
                MessageBox.Show(this, msg, "PixivRenamer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var report = NetworkDiagnostics.WriteReport(_baseDir, "用户点击「测试网络」失败",
                cfg, resolution, probes);
            if (report.Length > 0) AppendLog("网络诊断报告：" + report);

            var text = new System.Text.StringBuilder();
            text.AppendLine("网络测试失败。");
            text.AppendLine();
            text.AppendLine(detail.ToString().TrimEnd());
            text.AppendLine();
            text.AppendLine(NetworkDiagnostics.Advice(report));

            var dialog = new Window
            {
                Title = "网络测试失败 —— 详情与处理建议",
                Owner = this,
                Width = 760,
                Height = 540,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (System.Windows.Media.Brush)FindResource("App.Surface"),
            };
            var grid = new Grid { Margin = new Thickness(18) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var box = new TextBox
            {
                Text = text.ToString(),
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontSize = 13,
                MinHeight = 320,
            };
            box.Style = (Style)FindResource("LogBox");
            box.Height = double.NaN;
            box.MinHeight = 320;
            grid.Children.Add(box);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            Grid.SetRow(buttons, 1);
            var copy = new Button { Content = "复制详情", Width = 100, Margin = new Thickness(0, 0, 8, 0) };
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(text.ToString()); AppendLog("网络测试详情已复制到剪贴板"); } catch { }
            };
            var detect = new Button { Content = "检测本地代理", Width = 120, Margin = new Thickness(0, 0, 8, 0) };
            detect.Click += (_, _) => { dialog.Close(); DetectProxy_Click(sender, e); };
            var openDir = new Button { Content = "打开诊断目录", Width = 120, Margin = new Thickness(0, 0, 8, 0) };
            openDir.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", NetworkDiagnostics.ReportDir(_baseDir)); } catch { }
            };
            var close = new Button { Content = "关闭", Width = 90, IsCancel = true };
            close.Click += (_, _) => dialog.Close();
            buttons.Children.Add(copy);
            buttons.Children.Add(detect);
            buttons.Children.Add(openDir);
            buttons.Children.Add(close);
            grid.Children.Add(buttons);
            dialog.Content = grid;
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            AppendLog("网络测试异常：" + ex.Message);
            MessageBox.Show(this, "网络测试异常：\n" + ex.Message, "PixivRenamer",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AutoLogin_Click(object sender, RoutedEventArgs e)
    {
        var account = PixivAccountTextBox.Text.Trim();
        var password = PixivPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(password))
        {
            MessageBox.Show(this, "请先填写 pixiv 账号（邮箱或 pixiv ID）和密码。", "PixivRenamer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AppendLog("正在尝试登录 pixiv 获取 Cookie");
        try
        {
            // 探测与登录复用同一次代理侦测结果，避免重复扫描端口
            var loginCfg = CollectConfig();
            var loginResolution = await Task.Run(() => ProxyResolver.Resolve(loginCfg, AppendLog));
            var (netOk, netReason) = await ProbePixivAsync(loginResolution);
            if (!netOk)
            {
                var msg = "无法访问 pixiv，自动登录无法进行：" + netReason +
                          "\n\n请确认代理软件（Clash 等）已启动，然后在「网络与代理」处点「检测本地代理」并选择可用端口，" +
                          "或把代理模式改成「自动侦测」。\n\n也可以点击打开 pixiv 登录页，在浏览器登录后手动复制 Cookie。";
                AppendLog(msg.Replace("\n", " "));
                MessageBox.Show(this, msg, "PixivRenamer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            loginCfg.Proxy = loginResolution.Chosen;
            loginCfg.UseSystemProxy = loginResolution.Chosen.Length == 0 && loginCfg.ProxyMode != "direct";
            var service = new PixivLoginService(loginCfg);
            var result = await service.LoginAsync(account, password, CancellationToken.None);
            if (result.Success)
            {
                CookieTextBox.Text = result.Cookie;
                AppendLog("Cookie 获取成功，已填入 Cookie 输入框。");
                MessageBox.Show(this,
                    "Cookie 获取成功，已自动填入。\n\n" +
                    "建议点击保存配置把 Cookie 保存到 PixivRenamer.json。\n\n" +
                    "pixiv 可能因异地登录/频繁登录要求人机验证；如果之后查询失败，请改用浏览器手动复制 Cookie。",
                    "PixivRenamer", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                AppendLog("自动获取 Cookie 失败：" + result.Message);
                MessageBox.Show(this, result.Message + "\n\n可以点击打开 pixiv 登录页，在浏览器登录后手动复制 Cookie 粘贴到上方。",
                    "PixivRenamer", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            AppendLog("自动获取 Cookie 异常：" + ex.Message);
            MessageBox.Show(this, "自动获取 Cookie 失败：\n" + ex.Message, "PixivRenamer",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------------- 开始 / 取消 ----------------
    private void DryRunCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (StartButton == null) return;
        StartButton.Content = DryRunCheckBox.IsChecked == true ? "开始预览" : "开始整理";
        if (DryRunCheckBox.IsChecked == true)
            StatusText.Text = "预览模式：只生成报告，不会修改文件";
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        AppConfig cfg;
        try { cfg = CollectConfig(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "PixivRenamer"); return; }

        if (!Directory.Exists(cfg.SourceDir))
        {
            MessageBox.Show(this, "请先选择有效的插画文件夹。", "PixivRenamer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (cfg.CopyMode && string.IsNullOrWhiteSpace(cfg.OutputDir))
        {
            MessageBox.Show(this, "已勾选复制到输出目录，请先指定输出目录。", "PixivRenamer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!cfg.DryRun)
        {
            var answer = MessageBox.Show(this,
                $"即将正式修改文件（{(cfg.CopyMode ? "复制" : "移动")}）。\r\n\r\n" +
                "建议先用预览模式检查结果。\r\n确认继续吗？",
                "PixivRenamer", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        _running = true;
        _cts = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ProgressBar.IsIndeterminate = true;
        ProgressBar.Value = 0;
        StatusText.Text = cfg.DryRun ? "正在预览" : "正在整理";
        SetStatusDot("Work");
        _runCount++;
        AppendLog($"开始任务：{cfg.SourceDir}（{(cfg.DryRun ? "预览模式" : "正式执行")}）");
        var startedAt = DateTime.Now;

        var log = new Progress<string>(AppendLog);
        var progress = new Progress<ProgressInfo>(UpdateProgress);
        try
        {
            var engine = new RenamerEngine(cfg, log, progress);
            var result = await Task.Run(() => engine.RunAsync(_cts.Token), _cts.Token);
            AppendLog($"任务结束，返回码 {result.ExitCode}");

            var handled = result.Moved + result.Copied;
            var summary = $"处理完成：移动 {result.Moved}，复制 {result.Copied}，跳过 {result.Noop}，" +
                          $"未识别 {result.Unresolved}，失败 {result.Failed}。";
            if (cfg.DryRun) summary = "（预览模式，未修改文件）" + summary;
            if (handled == 0 && result.Unresolved > 0)
                summary += "\r\n\r\n所有文件都未识别：通常是网络无法访问 pixiv，请检查代理 / Cookie；\r\n" +
                           "可点击测试网络诊断，或先在浏览器登录后填写 Cookie。";
            if (result.Unresolved > 0 && cfg.MoveUnresolvedToFolder && !cfg.DryRun)
                summary += $"\r\n\r\n未识别文件已归入：{UnresolvedDir()}（可按原名再次查找来源）";
            else if (result.Unresolved > 0 && cfg.DryRun)
                summary += $"\r\n\r\n预览：正式执行时未识别文件会归入 {UnresolvedDir()}";

            AppendLog(summary.Replace("\r\n", " "));
            StatusText.Text = "完成";
            SetStatusDot(result.Failed > 0 ? "Warn" : "Ok");
            UpdateStats(_runCount, handled, result.Unresolved, result.Failed);
            AddTaskCard(
                $"{(cfg.DryRun ? "预览" : "整理")} · {startedAt:HH:mm:ss} · {Path.GetFileName(cfg.SourceDir.TrimEnd('\\'))}",
                $"可命名 {handled}，未识别 {result.Unresolved}，失败 {result.Failed}" +
                (cfg.DryRun ? "（未改动文件）" : ""),
                result.Unresolved > 0 || result.Failed > 0);
            MessageBox.Show(this, summary + "\r\n\r\n可点击「打开报告目录」查看 CSV 报告。",
                "PixivRenamer", MessageBoxButton.OK,
                result.ExitCode == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            AppendLog("任务已取消");
            StatusText.Text = "已取消";
            SetStatusDot("Idle");
        }
        catch (Exception ex)
        {
            AppendLog("任务失败：" + ex);
            StatusText.Text = "失败";
            SetStatusDot("Error");
            AddTaskCard($"失败 · {startedAt:HH:mm:ss}", ex.Message, true);
            MessageBox.Show(this, "任务失败：\n" + ex.Message, "PixivRenamer",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _running = false;
            StartButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            ProgressBar.IsIndeterminate = false;
            if (_closePending) Close();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_running) return;
        _cts?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "正在取消";
        AppendLog("已请求取消，等待当前请求结束");
    }

    private void UpdateProgress(ProgressInfo info)
    {
        if (info.Total <= 0)
        {
            ProgressBar.IsIndeterminate = true;
            StatusText.Text = info.Phase;
            SetStatusDot("Work");
        }
        else
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Maximum = info.Total;
            ProgressBar.Value = info.Current;
            var prefix = info.Phase.Contains("冷却") ? "⏳ " : "";
            StatusText.Text = $"{prefix}{info.Phase}  {info.Current} / {info.Total}";
            SetStatusDot(info.Phase.Contains("冷却") ? "Warn" : "Work");
        }
    }

    /// <summary>状态点颜色：Idle / Work / Ok / Warn / Error。</summary>
    private void SetStatusDot(string kind)
    {
        var brush = kind switch
        {
            "Work" => (System.Windows.Media.Brush)FindResource("App.Accent"),
            "Ok" => (System.Windows.Media.Brush)FindResource("App.Success"),
            "Warn" => (System.Windows.Media.Brush)FindResource("App.Warning"),
            "Error" => (System.Windows.Media.Brush)FindResource("App.Danger"),
            _ => (System.Windows.Media.Brush)FindResource("App.TextFaint"),
        };
        if (StatusDot != null) StatusDot.Fill = brush;
    }

    /// <summary>把一次任务的结果记进右侧「最近任务」卡片。</summary>
    private void AddTaskCard(string title, string detail, bool warn)
    {
        if (TaskList == null) return;
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Foreground = warn
                ? (System.Windows.Media.Brush)FindResource("App.Warning")
                : (System.Windows.Media.Brush)FindResource("App.Text"),
        });
        stack.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("App.TextMuted"),
        });
        TaskList.Items.Insert(0, stack);
        while (TaskList.Items.Count > 12) TaskList.Items.RemoveAt(TaskList.Items.Count - 1);
    }

    private void UpdateStats(long tasks, long resolved, long unresolved, long failed)
    {
        if (StatTasksText == null) return;
        StatTasksText.Text = tasks >= 0 ? tasks.ToString() : "—";
        StatResolvedText.Text = resolved >= 0 ? resolved.ToString() : "—";
        StatUnresolvedText.Text = unresolved >= 0 ? unresolved.ToString() : "—";
        StatFailedText.Text = failed >= 0 ? failed.ToString() : "—";
    }

    private void AppendLog(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        if (LogEmptyHint != null) LogEmptyHint.Visibility = Visibility.Collapsed;
        if (LogTextBox.Text.Length > 1_000_000) LogTextBox.Clear();
        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogTextBox.ScrollToEnd();
    }

    // ---------------- 菜单 / 工具 ----------------
    private void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var cfg = CollectConfig(false); // 保存用户填写的原始值，不把默认报告路径写死
            cfg.Save(ConfigPath);
            AppendLog("配置已保存：" + ConfigPath);
            MessageBox.Show(this, "配置已保存到 exe 同目录的 PixivRenamer.json。", "PixivRenamer",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog("保存配置失败：" + ex.Message);
            MessageBox.Show(this, "保存配置失败：\n" + ex.Message, "PixivRenamer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadConfig_Click(object sender, RoutedEventArgs e) => LoadConfigFromDisk();
    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogTextBox.Clear();

    private void SelfTest_Click(object sender, RoutedEventArgs e)
    {
        var result = SelfTest.RunAll();
        var text = result.ToString();
        try { File.WriteAllText(Path.Combine(_baseDir, "selftest.log"), text, new System.Text.UTF8Encoding(true)); } catch { }
        AppendLog(text.TrimEnd());
        MessageBox.Show(this, result.Fail == 0 ? "自检通过。" : "自检存在失败项，请查看日志。", "PixivRenamer",
            MessageBoxButton.OK, result.Fail == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var cfg = CollectConfig();
        var path = string.IsNullOrWhiteSpace(cfg.LogPath) ? Path.Combine(_baseDir, "Logs", "PixivRenamer.log") : cfg.LogPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (!File.Exists(path))
            File.AppendAllText(path, $"[{TextUtils.NowStamp()}] PixivRenamer {AppConfig.VersionText}{Environment.NewLine}",
                new System.Text.UTF8Encoding(true));
        if (File.Exists(path)) OpenWithShell(path);
        else MessageBox.Show(this, "日志文件尚未生成。", "PixivRenamer", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenCache_Click(object sender, RoutedEventArgs e)
    {
        var cfg = CollectConfig();
        var dir = string.IsNullOrWhiteSpace(cfg.CacheDir) ? Path.Combine(_baseDir, "cache") : cfg.CacheDir;
        Directory.CreateDirectory(dir);
        OpenFolder(dir);
    }

    private void OpenReadme_Click(object sender, RoutedEventArgs e)
    {
        var candidates = new[]
        {
            Path.Combine(_baseDir, "使用说明.txt"),
            Path.Combine(_baseDir, "README.md"),
            Path.Combine(_baseDir, "..", "..", "README.md"),
        };
        foreach (var path in candidates)
            if (File.Exists(path)) { OpenWithShell(path); return; }
        MessageBox.Show(this, "未找到使用说明文件。", "PixivRenamer", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>关于窗口：版本、项目地址、作者、许可、声明，并可当场检查更新。</summary>
    private void About_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "关于 PixivRenamer",
            Owner = this,
            Width = 660,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("App.Surface"),
        };
        var grid = new Grid { Margin = new Thickness(18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var badge = new Border
        {
            Width = 52, Height = 52, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 14, 0),
            Background = (System.Windows.Media.Brush)FindResource("App.Accent"),
        };
        badge.Child = new TextBlock
        {
            Text = "P", Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.Bold,
            FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(badge);
        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = $"PixivRenamer {AppConfig.VersionText}",
            FontSize = 20, FontWeight = FontWeights.SemiBold,
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = AppInfo.Tagline + "　（WPF / .NET 8）",
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("App.TextMuted"),
        });
        header.Children.Add(titleStack);
        grid.Children.Add(header);

        var body = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 16, 0, 0),
            FontSize = 13,
            Text =
                $"作者：{AppInfo.Author}\n" +
                $"项目主页：{AppInfo.RepositoryUrl}\n" +
                $"问题反馈：{AppInfo.IssuesUrl}\n" +
                $"开源许可：{AppInfo.License}（{AppInfo.LicenseUrl}）\n" +
                $"本机路径：{Elevation.ExecutablePath()}\n" +
                $"运行权限：{(Elevation.IsElevated ? "管理员" : "普通用户")}\n\n" +
                "功能：\n" + AppInfo.Features + "\n\n" +
                "声明：\n" + AppInfo.Disclaimer,
        };
        body.Style = (Style)FindResource("LogBox");
        body.Height = double.NaN;
        body.MinHeight = 300;
        Grid.SetRow(body, 1);
        grid.Children.Add(body);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var check = new Button
        {
            Content = "检查更新", Width = 104, Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)FindResource("PrimaryButton"),
        };
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            check.Content = "检查中…";
            var result = await CheckForUpdateAsync(silent: true);
            check.IsEnabled = true;
            check.Content = "检查更新";
            if (result is { Ok: true, HasUpdate: true }) ShowUpdateDialog(result);
            else if (result is { Ok: true })
                MessageBox.Show(this, $"已是最新版本（{result.CurrentVersion}）。", "检查更新",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show(this, "检查更新失败：" + (result?.Error ?? "未知原因") +
                    "\n\n需要能访问 github.com；如果用了代理，请先在「网络与代理」处测试网络。",
                    "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        var project = new Button { Content = "项目主页", Width = 96, Margin = new Thickness(0, 0, 8, 0) };
        project.Click += (_, _) => OpenUrl(AppInfo.RepositoryUrl);
        var license = new Button { Content = "许可协议", Width = 96, Margin = new Thickness(0, 0, 8, 0) };
        license.Click += (_, _) => OpenUrl(AppInfo.LicenseUrl);
        var close = new Button { Content = "关闭", Width = 88, IsCancel = true };
        close.Click += (_, _) => dialog.Close();
        buttons.Children.Add(check);
        buttons.Children.Add(project);
        buttons.Children.Add(license);
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);

        dialog.Content = grid;
        dialog.ShowDialog();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_running) return;
        var answer = MessageBox.Show(this, "任务正在运行，是否取消并退出？", "PixivRenamer",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            _closePending = true;
            _cts?.Cancel();
        }
        e.Cancel = true;
    }
}