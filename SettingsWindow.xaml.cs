using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClipboardPro.Models;
using ClipboardPro.Services;

namespace ClipboardPro;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly HistoryStore _store;
    private bool _loading;

    public SettingsWindow(AppConfig cfg, HistoryStore store)
    {
        InitializeComponent();

        _cfg = cfg;
        _store = store;

        _loading = true;
        LoadFromConfig();
        _loading = false;

        // 主题切换后重绘动态资源
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    private void LoadFromConfig()
    {
        // 条数：0 表示不限制，滑块最小值为 0
        int v = _cfg.MaxEntries;
        MaxEntriesSlider.Value = Math.Min(Math.Max(v, 0), MaxEntriesSlider.Maximum);
        MaxEntriesBox.Text = v.ToString();
        UpdateMaxEntriesHint();

        StartupCheck.IsChecked = _cfg.RunAtStartup;
        PasteCheck.IsChecked = _cfg.PasteOnSelect;
        InterceptWinVCheck.IsChecked = _cfg.InterceptWinV;
        ShowTrayIconCheck.IsChecked = _cfg.ShowTrayIcon;
        StartupNotifyCheck.IsChecked = _cfg.StartupNotify;

        ThemeAuto.IsChecked = _cfg.Theme == "Auto";
        ThemeLight.IsChecked = _cfg.Theme == "Light";
        ThemeDark.IsChecked = _cfg.Theme == "Dark";

        DataPathText.Text = AppPaths.DataDir;
    }

    private void UpdateMaxEntriesHint()
    {
        int v = _cfg.MaxEntries;
        if (v == 0)
        {
            MaxEntriesHint.Text = "0 = 不限制条数（历史会一直累积，注意磁盘占用）";
        }
        else
        {
            MaxEntriesHint.Text = $"最多保留 {v} 条未固定记录；当前共 {_store.Count} 条";
        }
    }

    private void MaxEntriesSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;

        int v = (int)Math.Round(e.NewValue);
        MaxEntriesBox.Text = v.ToString();
        _cfg.MaxEntries = v;
        UpdateMaxEntriesHint();
    }

    private void MaxEntriesBox_LostFocus(object sender, RoutedEventArgs e)
        => CommitMaxEntriesText();

    private void MaxEntriesBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitMaxEntriesText();
            e.Handled = true;
        }
    }

    private void CommitMaxEntriesText()
    {
        if (int.TryParse(MaxEntriesBox.Text?.Trim(), out int v))
        {
            v = Math.Max(0, Math.Min(v, 99999));
            _cfg.MaxEntries = v;
            MaxEntriesSlider.Value = Math.Min(v, MaxEntriesSlider.Maximum);
            MaxEntriesBox.Text = v.ToString();
            UpdateMaxEntriesHint();
        }
        else
        {
            MaxEntriesBox.Text = _cfg.MaxEntries.ToString();
        }
    }

    private void StartupCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _cfg.RunAtStartup = StartupCheck.IsChecked == true;
    }

    private void PasteCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _cfg.PasteOnSelect = PasteCheck.IsChecked == true;
    }

    private void InterceptWinVCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _cfg.InterceptWinV = InterceptWinVCheck.IsChecked == true;
    }

    private void ShowTrayIconCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _cfg.ShowTrayIcon = ShowTrayIconCheck.IsChecked == true;
    }

    private void StartupNotifyCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _cfg.StartupNotify = StartupNotifyCheck.IsChecked == true;
    }

    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        if (ThemeLight.IsChecked == true) _cfg.Theme = "Light";
        else if (ThemeDark.IsChecked == true) _cfg.Theme = "Dark";
        else _cfg.Theme = "Auto";

        ThemeManager.Apply(_cfg.Theme);
    }

    private void OnThemeChanged()
    {
        InvalidateVisual();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitMaxEntriesText();

        _cfg.RunAtStartup = StartupCheck.IsChecked == true;
        _cfg.PasteOnSelect = PasteCheck.IsChecked == true;
        _cfg.InterceptWinV = InterceptWinVCheck.IsChecked == true;
        _cfg.ShowTrayIcon = ShowTrayIconCheck.IsChecked == true;
        _cfg.StartupNotify = StartupNotifyCheck.IsChecked == true;

        _cfg.Save();

        if (Application.Current is App app)
        {
            app.OnConfigChanged();
        }

        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // 未保存直接关闭时，把主题恢复为已保存的值
        ThemeManager.Apply(_cfg.Theme);
        _cfg.Save();
        Close();
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureDirs();
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.DataDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开文件夹：{ex.Message}", "剪贴板",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ChangeDataDir_Click(object sender, RoutedEventArgs e)
    {
        string currentDir = AppPaths.DataDir;

        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择剪贴板数据的保存位置",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        string newPath = dlg.SelectedPath;
        if (string.IsNullOrWhiteSpace(newPath)) return;

        if (string.Equals(
                Path.GetFullPath(newPath).TrimEnd('\\', '/'),
                Path.GetFullPath(currentDir).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "新位置与当前数据位置相同。", "剪贴板",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 拒绝磁盘根目录，避免数据文件散落到盘符根
        string root = Path.GetPathRoot(newPath);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(Path.GetFullPath(newPath).TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "请选择一个子目录，不要直接选择磁盘根目录（如 D:\\）。", "剪贴板",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 目标目录若已存在且含无关文件，给出提醒
        if (Directory.Exists(newPath))
        {
            bool hasOtherFiles = Directory.EnumerateFileSystemEntries(newPath).Any(p =>
            {
                string n = Path.GetFileName(p);
                return n != "history.json" && n != "blobs" && n != "config.json" && n != "clipboardpro.log";
            });
            if (hasOtherFiles)
            {
                var r2 = MessageBox.Show(this,
                    "所选目录中已存在其他文件，剪贴板数据将与它们混放在一起。\n建议使用一个空目录或独立目录。\n\n仍要继续吗？",
                    "更改数据位置", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (r2 != MessageBoxResult.OK) return;
            }
        }

        var r = MessageBox.Show(this,
            $"将把全部历史记录迁移到：\n{newPath}\n\n迁移完成后应用会自动重启，是否继续？",
            "更改数据位置", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;

        string oldOverride = _cfg.DataDirOverride;
        try
        {
            AppPaths.EnsureDirs();
            Directory.CreateDirectory(newPath);

            // 迁移历史索引
            if (File.Exists(AppPaths.IndexPath))
                File.Copy(AppPaths.IndexPath, Path.Combine(newPath, "history.json"), true);

            // 迁移图片数据
            if (Directory.Exists(AppPaths.BlobDir))
            {
                var blobTarget = Path.Combine(newPath, "blobs");
                Directory.CreateDirectory(blobTarget);
                foreach (var f in Directory.GetFiles(AppPaths.BlobDir))
                    File.Copy(f, Path.Combine(blobTarget, Path.GetFileName(f)), true);
            }

            // 切换数据目录。注意顺序：先把指引写入旧位置的 config（应用启动时
            // 总是从默认/旧位置开始读取指引），再切换目录并把完整配置写入新位置。
            _cfg.DataDirOverride = newPath;
            _cfg.Save();
            AppPaths.SetCustomDir(newPath);
            _cfg.Save();

            AppPaths.Log($"数据目录已迁移至 {newPath}，准备重启。");
        }
        catch (Exception ex)
        {
            try
            {
                _cfg.DataDirOverride = oldOverride;
                AppPaths.SetCustomDir(oldOverride);
                _cfg.Save();
            }
            catch { }

            MessageBox.Show(this, $"迁移失败：{ex.Message}\n已恢复原数据位置。", "剪贴板",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 注意：重启在迁移成功之后、回滚保护之外执行，
        // 即使重启环节出问题也不会误报"迁移失败"而回滚配置。
        MessageBox.Show(this, "迁移完成，应用即将重启。", "剪贴板",
            MessageBoxButton.OK, MessageBoxImage.Information);
        (Application.Current as App)?.RequestRestart();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(this,
            "将删除全部历史记录，包括已固定的条目，且不可撤销。\n是否继续？",
            "清空全部记录", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (r != MessageBoxResult.OK) return;

        _store.ClearAll();
        UpdateMaxEntriesHint();

        MessageBox.Show(this, "已清空全部记录。", "剪贴板",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>标题栏拖动：按住标题区域可移动设置窗口。</summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && IsInsideButton(src)) return;
        try { DragMove(); }
        catch { /* 拖动被系统取消等场景，忽略 */ }
    }

    private static bool IsInsideButton(DependencyObject node)
    {
        while (node != null)
        {
            if (node is Button) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>在桌面创建指向本程序的快捷方式。</summary>
    private void CreateShortcut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string lnk = Path.Combine(desktop, "剪贴板历史.lnk");
            string exe = Environment.ProcessPath;

            if (string.IsNullOrEmpty(exe))
                throw new InvalidOperationException("无法确定程序路径");

            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                throw new InvalidOperationException("系统脚本组件不可用");

            dynamic shell = Activator.CreateInstance(shellType);
            dynamic shortcut = shell.CreateShortcut(lnk);
            shortcut.TargetPath = exe;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
            shortcut.IconLocation = exe + ",0";
            shortcut.Description = "剪贴板历史";
            shortcut.Save();

            MessageBox.Show(this, $"已创建桌面快捷方式：\n{lnk}", "剪贴板",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"创建快捷方式失败：{ex.Message}", "剪贴板",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        base.OnClosed(e);
    }
}