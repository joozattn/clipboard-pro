using System;
using System.Text.Json.Serialization;

namespace ClipboardPro.Models;

/// <summary>剪贴板条目类型。</summary>
public enum ClipKind
{
    Text = 0,
    Image = 1,
    Files = 2
}

/// <summary>
/// 单条剪贴板历史记录。仅保存元数据；大体积内容（图片）落在 blobs 目录，
/// 通过 BlobFile 引用，避免主索引 JSON 膨胀。
/// </summary>
public class ClipItem
{
    /// <summary>稳定唯一标识，用于去重与 blob 文件命名。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public ClipKind Kind { get; set; } = ClipKind.Text;

    /// <summary>文本内容（Kind=Text）；文件列表则为换行拼接的路径（Kind=Files）。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>blob 文件名（相对 blobs 目录）。图片内容存放于此。</summary>
    public string BlobFile { get; set; }

    /// <summary>内容指纹，用于去重（同类同内容视为同一条）。</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>最后写入时间（本地时间）。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>是否已固定。固定项不参与条数淘汰，永久保留。</summary>
    public bool Pinned { get; set; }

    /// <summary>图片像素宽高（仅 Kind=Image 时有意义，用于占位与缩略比例）。</summary>
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }

    [JsonIgnore]
    public bool IsImage => Kind == ClipKind.Image;

    [JsonIgnore]
    public bool IsFiles => Kind == ClipKind.Files;

    /// <summary>用于界面单行预览的文本。</summary>
    [JsonIgnore]
    public string Preview
    {
        get
        {
            if (Kind == ClipKind.Image) return string.Empty;
            if (Kind == ClipKind.Files)
            {
                var lines = Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 1) return System.IO.Path.GetFileName(lines[0]);
                return $"{System.IO.Path.GetFileName(lines[0])} 等 {lines.Length} 个项目";
            }
            var t = Text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
            return t;
        }
    }

    /// <summary>界面副标题：类型与时间。</summary>
    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            string type = Kind switch
            {
                ClipKind.Image => "图片",
                ClipKind.Files => "文件",
                _ => LooksLikeUrl() ? "链接" : "文本"
            };
            return $"{type} · {DescribeTime(CreatedAt)}";
        }
    }

    private bool LooksLikeUrl()
    {
        var s = Text.Trim();
        return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeTime(DateTime t)
    {
        var span = DateTime.Now - t;
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
        if (t.Date == DateTime.Today) return t.ToString("HH:mm");
        if (t.Date == DateTime.Today.AddDays(-1)) return $"昨天 {t:HH:mm}";
        if (span.TotalDays < 365) return t.ToString("M月d日 HH:mm");
        return t.ToString("yyyy年M月d日");
    }
}