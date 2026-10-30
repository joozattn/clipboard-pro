using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipboardPro.Models;

namespace ClipboardPro.Services;

/// <summary>
/// 剪贴板变更监听器。
///
/// 关键设计：WPF 的 System.Windows.Clipboard 只能在 STA 线程调用。
/// 因此本监听器运行在一条专用的 STA 线程上，自带消息泵（Dispatcher.Run），
/// 收到 WM_CLIPBOARDUPDATE 后在同一 STA 线程上直接读取剪贴板，
/// 避免线程池（MTA）调用剪贴板导致的静默失败。
///
/// 隐私处理：尊重应用写入的排除标记——
///   ExcludeClipboardContentFromMonitorProcessing / CanIncludeInClipboardHistory=0
/// 命中则整条跳过，密码管理器等敏感内容不会进入历史。
/// </summary>
public class ClipboardMonitor : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly HistoryStore _store;
    private readonly AppConfig _cfg;

    private Thread _thread;
    private Dispatcher _dispatcher;
    private HwndSource _source;
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _disposed;

    /// <summary>自身写入剪贴板时置位，避免回环捕获。</summary>
    public static volatile bool SuppressCapture;

    /// <summary>捕获到新条目时触发（在监听 STA 线程，UI 需自行 Dispatcher 切换）。</summary>
    public event Action<ClipItem> ItemCaptured;

    public ClipboardMonitor(HistoryStore store, AppConfig cfg)
    {
        _store = store;
        _cfg = cfg;
    }

    /// <summary>启动专用 STA 监听线程。</summary>
    public void Start()
    {
        if (_thread != null) return;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ClipboardPro.ClipboardListener"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;

            var parameters = new HwndSourceParameters("ClipboardProMessageWindow")
            {
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                WindowStyle = 0,
                ExtendedWindowStyle = 0
            };

            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
            _hwnd = _source.Handle;

            if (!AddClipboardFormatListener(_hwnd))
            {
                AppPaths.Log($"AddClipboardFormatListener 失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
            }
            else
            {
                AppPaths.Log("剪贴板监听已启动（STA 线程）。");
            }

            // 阻塞式消息循环，直到 Dispose 触发关闭
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            AppPaths.Log($"剪贴板监听线程异常：{ex.Message}");
        }
        finally
        {
            if (_hwnd != IntPtr.Zero)
            {
                RemoveClipboardFormatListener(_hwnd);
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

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            if (!SuppressCapture)
            {
                CaptureWithRetry();
            }
            handled = false;
        }
        return IntPtr.Zero;
    }

    /// <summary>在 STA 线程上带重试地读取剪贴板（剪贴板常被他进程短暂占用）。</summary>
    private void CaptureWithRetry()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (TryCaptureOnce()) return;
                return; // 明确判断为"无需记录"，不重试
            }
            catch (COMException)
            {
                // 剪贴板被占用，重试
            }
            catch (Exception ex)
            {
                AppPaths.Log($"读取剪贴板异常：{ex.Message}");
                return;
            }

            Thread.Sleep(60);
        }

        AppPaths.Log("读取剪贴板重试耗尽，本次跳过。");
    }

    /// <summary>单次尝试读取。返回 true 表示已处理（含主动跳过）。</summary>
    private bool TryCaptureOnce()
    {
        if (!ClipboardContainsContent()) return true;
        if (IsExcludedBySourceApp()) return true;

        // 1) 文件拖放优先（资源管理器复制文件时同时带文件名文本，需优先按文件处理）
        var files = SafeGetFileDrop();
        if (files != null && files.Count > 0)
        {
            var it = _store.AddFiles(files);
            if (it != null) ItemCaptured?.Invoke(it);
            return true;
        }

        // 2) 位图
        if (Clipboard.ContainsImage())
        {
            var img = SafeGetImage();
            if (img != null)
            {
                var it = _store.AddImage(img);
                if (it != null) ItemCaptured?.Invoke(it);
                return true;
            }
        }

        // 3) 纯文本
        var text = SafeGetText();
        if (!string.IsNullOrEmpty(text) && text.Length <= _cfg.MaxTextLength)
        {
            var it = _store.AddText(text);
            if (it != null) ItemCaptured?.Invoke(it);
            return true;
        }

        return true;
    }

    private static bool ClipboardContainsContent()
    {
        try
        {
            return Clipboard.ContainsText() || Clipboard.ContainsImage() || Clipboard.ContainsFileDropList();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查来源应用是否声明"不要纳入剪贴板历史"。
    /// 密码管理器（1Password、KeePass 等）会写入这些格式。
    /// </summary>
    private static bool IsExcludedBySourceApp()
    {
        try
        {
            if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing"))
                return true;

            if (Clipboard.ContainsData("CanIncludeInClipboardHistory"))
            {
                var data = Clipboard.GetData("CanIncludeInClipboardHistory");
                var value = ReadDwordish(data);
                if (value == 0) return true;
            }
        }
        catch
        {
            // 读取标记失败不影响主流程
        }
        return false;
    }

    /// <summary>把剪贴板中的标志位按 DWORD / 字节流两种常见形态解析为整数。</summary>
    private static int? ReadDwordish(object data)
    {
        if (data == null) return null;

        if (data is int i) return i;

        if (data is MemoryStream ms)
        {
            var buf = ms.ToArray();
            if (buf.Length >= 4) return BitConverter.ToInt32(buf, 0);
            if (buf.Length >= 1) return buf[0];
        }

        if (data is byte[] bytes)
        {
            if (bytes.Length >= 4) return BitConverter.ToInt32(bytes, 0);
            if (bytes.Length >= 1) return bytes[0];
        }

        return null;
    }

    private static List<string> SafeGetFileDrop()
    {
        try
        {
            if (!Clipboard.ContainsFileDropList()) return null;
            var col = Clipboard.GetFileDropList();
            var list = new List<string>();
            foreach (string s in col) if (!string.IsNullOrEmpty(s)) list.Add(s);
            return list;
        }
        catch { return null; }
    }

    private static BitmapSource SafeGetImage()
    {
        try
        {
            var src = Clipboard.GetImage();
            if (src == null) return null;
            if (src.CanFreeze) src.Freeze();
            return src;
        }
        catch { return null; }
    }

    private static string SafeGetText()
    {
        try { return Clipboard.GetText(); }
        catch { return null; }
    }

    /// <summary>把条目写回系统剪贴板（供粘贴使用）。</summary>
    public static bool WriteToClipboard(ClipItem item)
    {
        if (item == null) return false;

        SuppressCapture = true;
        try
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var payload = HistoryStore.GetPastePayload(item);
                    if (payload == null) return false;

                    if (payload is string s)
                    {
                        Clipboard.SetText(s);
                    }
                    else if (payload is string[] paths)
                    {
                        var col = new System.Collections.Specialized.StringCollection();
                        col.AddRange(paths);
                        Clipboard.SetFileDropList(col);
                    }
                    else if (payload is BitmapSource bmp)
                    {
                        Clipboard.SetImage(bmp);
                    }
                    else
                    {
                        return false;
                    }

                    return true;
                }
                catch (COMException)
                {
                    Thread.Sleep(60);
                }
            }
            return false;
        }
        finally
        {
            // 给监听器留出观察窗口，避免紧接着的 WM_CLIPBOARDUPDATE 被误捕获
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(250);
                SuppressCapture = false;
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var d = _dispatcher;
        if (d != null && !d.HasShutdownStarted)
        {
            d.BeginInvokeShutdown(DispatcherPriority.Normal);
        }

        if (_thread != null && _thread != Thread.CurrentThread)
        {
            _thread.Join(3000);
        }
        _thread = null;
    }
}
