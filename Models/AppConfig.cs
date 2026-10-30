using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipboardPro.Models;

/// <summary>
/// 应用配置。持久化到 %APPDATA%\ClipboardPro\config.json，
/// 用户可直接编辑该文件调整行为（含历史保存条数）。
/// </summary>
public class AppConfig
{
    /// <summary>
    /// 历史记录保存条数上限。0 表示不限制。
    /// 固定（Pinned）条目不计入此上限，也不被淘汰。
    /// </summary>
    public int MaxEntries { get; set; } = 500;

    /// <summary>开机自动启动。</summary>
    public bool RunAtStartup { get; set; } = true;

    /// <summary>是否接管 Win+V（屏蔽系统自带的剪贴板面板，交给本应用）。</summary>
    public bool InterceptWinV { get; set; } = true;

    /// <summary>是否显示托盘图标。关闭后仍可通过面板右上角的设置入口进入设置。</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>自定义数据目录。空表示使用默认的 %APPDATA%\ClipboardPro。</summary>
    public string DataDirOverride { get; set; }

    /// <summary>面板上次位置（-1 表示未记忆，使用默认的底部居中）。</summary>
    public double PanelLeft { get; set; } = -1;
    public double PanelTop { get; set; } = -1;

    /// <summary>面板显示时自动聚焦搜索框。</summary>
    public bool FocusSearchOnOpen { get; set; } = true;

    /// <summary>点击条目后是否自动粘贴到前台窗口（而非仅复制）。</summary>
    public bool PasteOnSelect { get; set; } = true;

    /// <summary>
    /// 面板唤出热键。win+v 已被系统占用时自动回退到此备用组合。
    /// 格式："Ctrl+Alt+V"。
    /// </summary>
    public string FallbackHotkey { get; set; } = "Ctrl+Alt+V";

    /// <summary>是否已提示过 Win+V 热键冲突。</summary>
    public bool HotkeyConflictShown { get; set; }

    /// <summary>主题：Auto / Light / Dark。</summary>
    public string Theme { get; set; } = "Auto";

    /// <summary>最大单条文本长度，超出则截断（防止超大内容拖慢索引）。</summary>
    public int MaxTextLength { get; set; } = 2_000_000;

    /// <summary>单文件大小上限（字节），超过则不记录。</summary>
    public long MaxBlobBytes { get; set; } = 32L * 1024 * 1024;

    [JsonIgnore]
    public bool IsUnlimited => MaxEntries <= 0;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string ConfigPath => Path.Combine(AppPaths.DataDir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                if (cfg != null)
                {
                    cfg.Normalize();
                    // 配置可能携带自定义数据目录，立即生效
                    AppPaths.SetCustomDir(cfg.DataDirOverride);
                    return cfg;
                }
            }
        }
        catch
        {
            // 配置损坏时回落默认值，不阻塞启动。
        }

        var fresh = new AppConfig();
        // 若当前已处于自定义数据目录（例如新位置的 config 意外丢失），
        // 重建的配置必须保留目录指引，避免应用悄悄退回旧目录。
        if (AppPaths.CustomDir != null) fresh.DataDirOverride = AppPaths.CustomDir;
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        Normalize();
        AppPaths.EnsureDirs();
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    private void Normalize()
    {
        if (MaxEntries < 0) MaxEntries = 0;
        if (MaxTextLength < 1024) MaxTextLength = 1024;
        if (MaxBlobBytes < 1024 * 1024) MaxBlobBytes = 1024 * 1024;
        if (string.IsNullOrWhiteSpace(FallbackHotkey)) FallbackHotkey = "Ctrl+Alt+V";
        if (string.IsNullOrWhiteSpace(DataDirOverride)) DataDirOverride = null;
        Theme = string.IsNullOrWhiteSpace(Theme) ? "Auto" : Theme;
    }
}

/// <summary>应用数据目录定位。支持自定义目录（随配置迁移）。</summary>
public static class AppPaths
{
    /// <summary>自定义数据目录；null 表示使用默认位置。</summary>
    public static string CustomDir { get; private set; }

    /// <summary>切换数据目录。空/null 恢复默认位置。</summary>
    public static void SetCustomDir(string path)
    {
        CustomDir = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
    }

    public static string DataDir =>
        CustomDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipboardPro");

    public static string BlobDir => Path.Combine(DataDir, "blobs");
    public static string IndexPath => Path.Combine(DataDir, "history.json");
    public static string LogPath => Path.Combine(DataDir, "clipboardpro.log");

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(BlobDir);
    }

    public static void Log(string message)
    {
        try
        {
            EnsureDirs();
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { /* 日志失败不影响主流程 */ }
    }
}