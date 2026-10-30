using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using ClipboardPro.Models;

namespace ClipboardPro.Services;

/// <summary>
/// 全局热键注册。优先尝试 Win+V 接管系统剪贴板面板；
/// 若失败（系统已占用），回退到配置中的备用组合（默认 Ctrl+Alt+V）。
///
/// 说明：Win+V 由 explorer 的 ShellExperienceHost 优先响应，
/// RegisterHotKey 通常无法抢占，因此实际可用热键多为备用组合。
/// </summary>
public class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private const int ID_PRIMARY = 0xA001;
    private const int ID_FALLBACK = 0xA002;

    private HwndSource _source;
    private IntPtr _hwnd = IntPtr.Zero;

    /// <summary>Win+V 是否成功注册（决定是否需要在界面提示冲突）。</summary>
    public bool WinVRegistered { get; private set; }

    /// <summary>备用热键是否注册成功。</summary>
    public bool FallbackRegistered { get; private set; }

    /// <summary>实际生效的热键描述，用于托盘提示。</summary>
    public string EffectiveHotkey { get; private set; } = "（未注册成功）";

    /// <summary>热键被按下。</summary>
    public event Action HotkeyPressed;

    public void Start()
    {
        var parameters = new HwndSourceParameters("ClipboardProHotkeyWindow")
        {
            Width = 0, Height = 0, PositionX = 0, PositionY = 0,
            WindowStyle = 0, ExtendedWindowStyle = 0
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        _hwnd = _source.Handle;

        // 1) 先试 Win+V
        WinVRegistered = RegisterHotKey(_hwnd, ID_PRIMARY, MOD_WIN | MOD_NOREPEAT, 0x56 /* V */);
        if (WinVRegistered)
        {
            EffectiveHotkey = "Win+V";
            AppPaths.Log("Win+V 注册成功，已接管系统剪贴板面板热键。");
        }
        else
        {
            AppPaths.Log($"Win+V 注册失败（错误码 {Marshal.GetLastWin32Error()}），系统已占用，改用备用热键。");
        }

        // 2) 再注册备用热键
        var (mods, vk, desc) = ParseHotkey(AppConfig.Load().FallbackHotkey);
        if (mods != 0)
        {
            FallbackRegistered = RegisterHotKey(_hwnd, ID_FALLBACK, mods | MOD_NOREPEAT, vk);
            if (FallbackRegistered)
            {
                EffectiveHotkey = WinVRegistered ? "Win+V" : desc;
                AppPaths.Log($"备用热键 {desc} 注册成功。");
            }
            else
            {
                AppPaths.Log($"备用热键 {desc} 注册失败（错误码 {Marshal.GetLastWin32Error()}）。");
            }
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == ID_PRIMARY || id == ID_FALLBACK)
            {
                HotkeyPressed?.Invoke();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>解析形如 "Ctrl+Alt+V" 的热键字符串。</summary>
    private static (uint mods, uint vk, string desc) ParseHotkey(string spec)
    {
        uint mods = 0;
        uint vk = 0;
        var parts = new System.Collections.Generic.List<string>();

        foreach (var raw in (spec ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = raw.Trim();
            switch (p.ToLowerInvariant())
            {
                case "ctrl":
                case "control": mods |= MOD_CONTROL; parts.Add("Ctrl"); break;
                case "alt": mods |= MOD_ALT; parts.Add("Alt"); break;
                case "shift": mods |= MOD_SHIFT; parts.Add("Shift"); break;
                case "win": mods |= MOD_WIN; parts.Add("Win"); break;
                default:
                    if (p.Length == 1)
                    {
                        vk = (uint)char.ToUpperInvariant(p[0]);
                        parts.Add(p.ToUpperInvariant());
                    }
                    else if (Enum.TryParse<Key>(p, true, out var key))
                    {
                        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                        parts.Add(p.ToUpperInvariant());
                    }
                    break;
            }
        }

        return (mods, vk, string.Join("+", parts));
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            if (WinVRegistered) UnregisterHotKey(_hwnd, ID_PRIMARY);
            if (FallbackRegistered) UnregisterHotKey(_hwnd, ID_FALLBACK);
            _hwnd = IntPtr.Zero;
        }
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }
}