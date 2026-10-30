using System;
using System.Runtime.InteropServices;
using System.Threading;
using ClipboardPro.Models;

namespace ClipboardPro.Services;

/// <summary>
/// 通过 WH_KEYBOARD_LL 低层键盘钩子接管 Win+V，并精确重建 Win 键事件。
///
/// 设计要点：
/// 1. 低层键盘钩子回调必须极快返回，否则会阻塞整个系统键盘输入，因此本类
///    把钩子安装到一条**独立专用线程**（自建消息循环），回调只做轻量状态机。
/// 2. 完全吞掉 Win 键的按下与抬起，由本类向系统「补发」正确成对的按键序列：
///    - 单独按 Win        → 补发一次完整 Win 击键，正常打开/关闭开始菜单；
///    - Win+V             → 吞掉 V 并触发面板，不补发，开始菜单不弹、无残留；
///    - Win+其他键(如 E/R/D) → 补发 Win 按下，让系统识别为 Win 组合键。
///    系统始终看到「成对」的 Win 键事件，绝不残留 Win 按住状态，从根上消除
///    “按字母却弹出一堆系统窗口”的问题。
/// 3. 补发的按键带有一个私有 dwExtraInfo 标记，钩子据此放行——因为 SendInput
///    是异步的，用「临时标志」会在回调返回后被复位而失效，标记则不受时序影响。
/// 4. 钩子全程 try/catch，任何异常都放行按键，绝不影响键盘输入。
/// </summary>
public class WinVInterceptor : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_QUIT = 0x0012;

    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_V = 0x56;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>补发按键的私有标记，钩子据此识别为自己注入的事件并放行。</summary>
    private static readonly IntPtr InjectMark = new IntPtr(0x43504C43); // "CLPC"

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

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

    private Thread _thread;
    private uint _threadId;
    private LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private volatile bool _disposed;

    /// <summary>是否接管 Win+V。关闭时钩子直接放行。跨线程读写，volatile 保证可见性。</summary>
    public volatile bool Enabled = true;

    /// <summary>是否已成功挂上钩子。</summary>
    public bool IsActive => Volatile.Read(ref _hookId) != IntPtr.Zero;

    /// <summary>钩子安装完成（无论成败）后触发，在钩子线程回调。</summary>
    public event Action Ready;

    // —— 状态机（仅在钩子线程读写）——
    private bool _winDown;      // 真实 Win 键是否按下（自跟踪）
    private bool _isWinV;       // 本次 Win 按下期间是否按了 V
    private bool _injectedDown; // 是否已补发过 Win 按下

    /// <summary>Win+V 被按下（钩子线程触发，订阅方需自行切回 UI 线程）。</summary>
    public event Action WinVPressed;

    public void Start()
    {
        if (_thread != null) return;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ClipboardPro.WinVHook"
        };
        _thread.Start();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();

        _proc = HookCallback;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);

        if (_hookId == IntPtr.Zero)
        {
            AppPaths.Log($"Win+V 低层钩子安装失败，错误码 {Marshal.GetLastWin32Error()}。");
        }
        else
        {
            AppPaths.Log("Win+V 低层钩子已接管（独立线程）。");
        }

        try { Ready?.Invoke(); }
        catch { /* 订阅方异常不影响钩子 */ }

        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        _proc = null;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode < 0 || !Enabled)
            {
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            // 自己补发的按键带标记，直接放行，不进入状态机
            IntPtr extra = Marshal.ReadIntPtr(lParam, 16); // KBDLLHOOKSTRUCT.dwExtraInfo
            if (extra == InjectMark)
            {
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            int msg = wParam.ToInt32();
            int vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode

            bool isWinKey = vk == VK_LWIN || vk == VK_RWIN;
            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            if (isWinKey && isDown)
            {
                _winDown = true;
                _isWinV = false;
                _injectedDown = false;
                return (IntPtr)1; // 吞 Win 按下，系统不会进入 Win 态
            }

            if (isWinKey && isUp)
            {
                _winDown = false;
                if (_injectedDown)
                {
                    InjectWinUp();   // 结束补发的 Win 组合态（期间有键，不会弹开始菜单）
                }
                else if (!_isWinV)
                {
                    InjectWinTap();  // 单独按 Win：补发一次完整击键，正常切换开始菜单
                }
                return (IntPtr)1; // 吞 Win 抬起
            }

            if (_winDown && isDown)
            {
                if (vk == VK_V)
                {
                    _isWinV = true;
                    try { WinVPressed?.Invoke(); }
                    catch { /* 订阅方异常不影响键盘 */ }
                    return (IntPtr)1; // 吞 V，弹出本应用面板
                }

                if (!_injectedDown)
                {
                    _injectedDown = true;
                    InjectWinDown();  // 补发 Win 按下，让系统识别 Win+组合键
                }
                // 放行该键，形成 Win+组合键
            }
        }
        catch
        {
            // 任何异常都放行，绝不影响键盘输入
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static INPUT MakeKey(ushort vk, uint flags, IntPtr extra) => new INPUT
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags, dwExtraInfo = extra } }
    };

    private void InjectWinDown()
    {
        SendInput(1, new[] { MakeKey(VK_LWIN, 0, InjectMark) }, Marshal.SizeOf<INPUT>());
    }

    private void InjectWinUp()
    {
        SendInput(1, new[] { MakeKey(VK_LWIN, KEYEVENTF_KEYUP, InjectMark) }, Marshal.SizeOf<INPUT>());
    }

    private void InjectWinTap()
    {
        SendInput(2, new[]
        {
            MakeKey(VK_LWIN, 0, InjectMark),
            MakeKey(VK_LWIN, KEYEVENTF_KEYUP, InjectMark)
        }, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_thread != null)
        {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            if (_thread != Thread.CurrentThread)
            {
                _thread.Join(2000);
            }
            _thread = null;
        }
    }
}
