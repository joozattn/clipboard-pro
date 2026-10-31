using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ClipboardPro.Models;
using ClipboardPro.Services;

namespace ClipboardPro;

public partial class App : Application
{
    private HistoryStore _store;
    private AppConfig _cfg;
    private ClipboardMonitor _monitor;
    private HotkeyManager _hotkeys;
    private WinVInterceptor _winv;
    private bool _restartPending;

    private System.Windows.Forms.NotifyIcon _tray;
    private MainWindow _panel;
    private SettingsWindow _settings;

    /// <summary>唤出热键的展示文本，供界面提示。</summary>
    public string EffectiveHotkeyText { get; private set; } = "Win+V";

    /// <summary>上一次的前台窗口，用于粘贴回目标。</summary>
    private IntPtr _previousForeground = IntPtr.Zero;

    #region Win32

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    #endregion

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 是否由开机自启（Run 项带 --autostart 参数）拉起
        bool isAutoStart = e.Args.Contains("--autostart");

        // 单实例：避免重复运行导致双份监听与热键冲突
        if (!AcquireSingleInstance())
        {
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 捕获 UI 线程未处理异常：记录日志，避免静默退出
        DispatcherUnhandledException += (_, args) =>
        {
            AppPaths.Log($"UI 线程未处理异常：{args.Exception}");
            args.Handled = true;
        };

        AppPaths.EnsureDirs();

        // 数据目录加载：配置里可能带有"数据位置指引"，循环跟随直到目录稳定
        //（支持多次链式迁移）。每次 Load 内部都会按配置切换 AppPaths 数据目录。
        _cfg = AppConfig.Load();
        for (int i = 0; i < 4; i++)
        {
            string before = AppPaths.DataDir;
            _cfg = AppConfig.Load();
            if (string.Equals(AppPaths.DataDir, before, StringComparison.OrdinalIgnoreCase)) break;
        }

        // 目录切换完成后，再确保实际数据目录存在并记录启动日志，
        // 避免日志被写到迁移前的旧目录。
        AppPaths.EnsureDirs();
        AppPaths.Log("===== 应用启动 =====");

        _store = new HistoryStore(_cfg);

        ThemeManager.Apply(_cfg.Theme);

        _panel = new MainWindow(_store, _cfg);

        StartMonitor();
        StartHotkey();
        StartWinVHook();

        EnsureTray();
        ApplyStartupSetting();

        AppPaths.Log($"启动完成。历史 {_store.Count} 条，热键 {EffectiveHotkeyText}。");

        // 开机自启 + 托盘图标关闭 + 用户开启提示：弹一次确认提示
        if (isAutoStart && _cfg.StartupNotify && !_cfg.ShowTrayIcon)
        {
            Dispatcher.BeginInvoke(new Action(ShowStartupNotify), DispatcherPriority.ApplicationIdle);
        }
    }

    #region 单实例

    private Mutex _mutex;

    private bool AcquireSingleInstance()
    {
        // 数据目录迁移会自动重启：短暂重试等待旧实例释放互斥体
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                _mutex = new Mutex(true, @"Global\ClipboardPro.SingleInstance.v1", out bool created);
                if (created) return true;
                _mutex.Dispose();
                _mutex = null;
            }
            catch
            {
                return true;
            }

            Thread.Sleep(200);
        }

        // 已有实例：唤醒它的面板
        try
        {
            var living = Process.GetProcessesByName("ClipboardPro");
            if (living.Length > 0)
            {
                ShowWindowAsync(living[0].MainWindowHandle, SW_RESTORE);
            }
        }
        catch { }
        return false;
    }

    #endregion

    #region 监听与热键

    private void StartMonitor()
    {
        _monitor = new ClipboardMonitor(_store, _cfg);
        _monitor.ItemCaptured += item =>
        {
            // 监听线程回调，统一切回 UI 线程再更新界面
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshTrayText();
                if (_panel != null && _panel.IsVisible)
                {
                    _panel.ShowPanel();
                }
            }), DispatcherPriority.Background);
        };
        _monitor.Start();
    }

    private void StartHotkey()
    {
        _hotkeys = new HotkeyManager();
        _hotkeys.HotkeyPressed += () =>
        {
            Dispatcher.BeginInvoke(new Action(TogglePanel), DispatcherPriority.Send);
        };
        _hotkeys.Start();

        EffectiveHotkeyText = _hotkeys.EffectiveHotkey;
    }

    private void StartWinVHook()
    {
        _winv = new WinVInterceptor();
        _winv.WinVPressed += () =>
        {
            Dispatcher.BeginInvoke(new Action(TogglePanel), DispatcherPriority.Send);
        };
        _winv.Ready += () =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateEffectiveHotkey();
                AppPaths.Log($"热键已就绪：{EffectiveHotkeyText}。");
                if (_cfg.InterceptWinV && !_winv.IsActive && !_cfg.HotkeyConflictShown)
                {
                    ShowHotkeyTipOnce();
                }
            }));
        };
        _winv.Enabled = _cfg.InterceptWinV;
        _winv.Start();
    }

    private void UpdateEffectiveHotkey()
    {
        if (_winv != null && _winv.IsActive && _cfg.InterceptWinV)
        {
            EffectiveHotkeyText = "Win+V";
        }
        else if (_hotkeys != null && _hotkeys.FallbackRegistered)
        {
            EffectiveHotkeyText = _hotkeys.EffectiveHotkey;
        }
        else
        {
            EffectiveHotkeyText = "（未注册成功）";
        }
    }

    private void TogglePanel()
    {
        // 打开前记录前台窗口，供粘贴回去
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != new System.Windows.Interop.WindowInteropHelper(_panel).Handle)
        {
            _previousForeground = fg;
        }

        _panel.ToggleVisibility();
    }

    public void ShowSettings()
    {
        if (_settings == null)
        {
            _settings = new SettingsWindow(_cfg, _store);
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        else
        {
            _settings.Activate();
        }
    }

    #endregion

    #region 粘贴

    /// <summary>把焦点还给上一个前台窗口并发送 Ctrl+V。</summary>
    public void PasteToPreviousWindow()
    {
        var target = _previousForeground;
        if (target == IntPtr.Zero) return;

        try
        {
            if (IsIconic(target)) ShowWindowAsync(target, SW_RESTORE);

            // 前台窗口切换受系统限制，需附加线程输入才能强制成功
            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint targetThread = GetWindowThreadProcessId(target, out _);
            uint selfThread = GetCurrentThreadId();

            if (fgThread != selfThread) AttachThreadInput(selfThread, fgThread, true);
            if (targetThread != selfThread) AttachThreadInput(selfThread, targetThread, true);

            SetForegroundWindow(target);

            if (targetThread != selfThread) AttachThreadInput(selfThread, targetThread, false);
            if (fgThread != selfThread) AttachThreadInput(selfThread, fgThread, false);

            // 给目标窗口接收焦点留出时间，再注入按键
            Thread.Sleep(60);
            SendCtrlV();
        }
        catch (Exception ex)
        {
            AppPaths.Log($"粘贴失败：{ex.Message}");
        }
    }

    private static void SendCtrlV()
    {
        var inputs = new INPUT[4];

        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].U.ki.wVk = VK_CONTROL;
        inputs[0].U.ki.dwFlags = 0;

        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].U.ki.wVk = VK_V;
        inputs[1].U.ki.dwFlags = 0;

        inputs[2].type = INPUT_KEYBOARD;
        inputs[2].U.ki.wVk = VK_V;
        inputs[2].U.ki.dwFlags = KEYEVENTF_KEYUP;

        inputs[3].type = INPUT_KEYBOARD;
        inputs[3].U.ki.wVk = VK_CONTROL;
        inputs[3].U.ki.dwFlags = KEYEVENTF_KEYUP;

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    #endregion

    #region 托盘

    /// <summary>按配置创建或隐藏托盘图标（设置里可关闭显示）。</summary>
    private void EnsureTray()
    {
        if (_cfg.ShowTrayIcon && _tray == null)
        {
            CreateTray();
        }
        else if (!_cfg.ShowTrayIcon && _tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
    }

    private void CreateTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "剪贴板历史",
            Visible = true,
            Icon = LoadTrayIcon()
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();

        var openItem = new System.Windows.Forms.ToolStripMenuItem("打开剪贴板历史");
        openItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            _previousForeground = GetForegroundWindow();
            _panel.ShowPanel();
        }));
        menu.Items.Add(openItem);

        var settingsItem = new System.Windows.Forms.ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ShowSettings));
        menu.Items.Add(settingsItem);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var countItem = new System.Windows.Forms.ToolStripMenuItem("查看统计");
        countItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ShowStats));
        menu.Items.Add(countItem);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ExitApp));
        menu.Items.Add(exitItem);

        _tray.ContextMenuStrip = menu;

        // 单击托盘图标直接打开面板
        _tray.MouseClick += (s, ev) =>
        {
            if (ev.Button == System.Windows.Forms.MouseButtons.Left)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _previousForeground = GetForegroundWindow();
                    _panel.ShowPanel();
                }));
            }
        };

        RefreshTrayText();
    }

    /// <summary>托盘图标：手绘小猫咪。</summary>
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            return DrawCatIcon();
        }
        catch
        {
            // 绘制失败退回系统图标，保证托盘始终有可见图标
            return System.Drawing.SystemIcons.Application;
        }
    }

    /// <summary>用 GDI+ 画一只橘猫作为托盘图标。</summary>
    private static System.Drawing.Icon DrawCatIcon()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            using var fur = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0xF6, 0xA5, 0x4C));
            using var dark = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x4A, 0x36, 0x28));
            using var pink = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0xF2, 0x9E, 0x9E));
            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0x4A, 0x36, 0x28), 1.2f);

            // 耳朵
            g.FillPolygon(fur, new[] { new System.Drawing.PointF(6, 13), new System.Drawing.PointF(9, 3), new System.Drawing.PointF(16, 9) });
            g.FillPolygon(fur, new[] { new System.Drawing.PointF(26, 13), new System.Drawing.PointF(23, 3), new System.Drawing.PointF(16, 9) });
            g.FillPolygon(pink, new[] { new System.Drawing.PointF(9.5f, 11), new System.Drawing.PointF(11, 6), new System.Drawing.PointF(14.5f, 9.5f) });
            g.FillPolygon(pink, new[] { new System.Drawing.PointF(22.5f, 11), new System.Drawing.PointF(21, 6), new System.Drawing.PointF(17.5f, 9.5f) });

            // 头
            g.FillEllipse(fur, 4, 8, 24, 20);

            // 眼睛
            g.FillEllipse(dark, 11, 17, 3, 4);
            g.FillEllipse(dark, 18, 17, 3, 4);

            // 鼻子与嘴
            g.FillEllipse(pink, 14.8f, 21.5f, 2.6f, 2f);
            g.DrawArc(pen, 12.4f, 22.5f, 3.6f, 3f, 15, 150);
            g.DrawArc(pen, 16, 22.5f, 3.6f, 3f, 15, 150);

            // 胡须
            g.DrawLine(pen, 2, 20, 9, 21.5f);
            g.DrawLine(pen, 2, 24, 9, 23.5f);
            g.DrawLine(pen, 23, 21.5f, 30, 20);
            g.DrawLine(pen, 23, 23.5f, 30, 24);
        }

        // 克隆图标并释放原生句柄，避免 HICON 泄漏
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var tmp = System.Drawing.Icon.FromHandle(hIcon);
            return (System.Drawing.Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private void RefreshTrayText()
    {
        if (_tray == null) return;
        var text = $"剪贴板历史 · {_store.Count} 条";
        // NotifyIcon 文本上限 63 字符
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    private void ShowStats()
    {
        var limit = _cfg.IsUnlimited ? "不限制" : $"{_cfg.MaxEntries} 条";
        MessageBox.Show(
            $"历史记录：{_store.Count} 条\n" +
            $"已固定：{_store.PinnedCount} 条\n" +
            $"条数上限：{limit}\n" +
            $"唤出热键：{EffectiveHotkeyText}\n\n" +
            $"数据位置：\n{AppPaths.DataDir}",
            "剪贴板统计", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowHotkeyTipOnce()
    {
        _cfg.HotkeyConflictShown = true;
        _cfg.Save();

        MessageBox.Show(
            "未能接管 Win+V，已改用备用热键 " + EffectiveHotkeyText + " 唤出面板。\n\n" +
            "若希望恢复 Win+V，请检查是否有其他程序占用了该热键。",
            "剪贴板历史", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>开机自启后的后台运行提示（托盘图标关闭时）。</summary>
    private void ShowStartupNotify()
    {
        MessageBox.Show(
            "剪贴板历史已在后台运行。\n\n" +
            "按 Win+V 即可唤出面板；需要设置请唤出面板后点右上角齿轮。\n\n" +
            "若不想再看到本提示，可在设置中关闭「开机时提示」。",
            "剪贴板历史", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExitApp()
    {
        // 托盘图标可能被用户隐藏（_tray 为 null）
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        Shutdown();
    }

    #endregion

    #region 开机自启

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ClipboardPro";

    private void ApplyStartupSetting()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            if (_cfg.RunAtStartup)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    // 带 --autostart 参数，供启动时识别"是否开机自启"
                    key.SetValue(RunValueName, $"\"{exe}\" --autostart", Microsoft.Win32.RegistryValueKind.String);
                }
            }
            else if (key.GetValue(RunValueName) != null)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"配置开机自启失败：{ex.Message}");
        }
    }

    /// <summary>供设置窗口在修改后调用。</summary>
    public void OnConfigChanged()
    {
        _cfg.Save();
        ApplyStartupSetting();
        ThemeManager.Apply(_cfg.Theme);

        if (_winv != null) _winv.Enabled = _cfg.InterceptWinV;
        UpdateEffectiveHotkey();

        EnsureTray();
        _panel.ShowPanel();
        RefreshTrayText();
    }

    /// <summary>请求退出并重新启动应用（数据目录迁移后使用）。</summary>
    public void RequestRestart()
    {
        _restartPending = true;
        // 清理统一在 OnExit 中完成（含托盘判空），这里直接关闭即可
        Shutdown();
    }

    public HistoryStore Store => _store;

    #endregion

    protected override void OnExit(ExitEventArgs e)
    {
        AppPaths.Log("===== 应用退出 =====");

        _monitor?.Dispose();
        _hotkeys?.Dispose();
        _winv?.Dispose();

        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        _mutex?.Dispose();

        // 数据目录迁移等场景：退出后立即以新配置重启
        if (_restartPending)
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    // 经资源管理器拉起，进程生命周期独立
                    System.Diagnostics.Process.Start("explorer.exe", exe);
                }
            }
            catch { }
        }

        base.OnExit(e);
    }
}