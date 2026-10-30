using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using ClipboardPro.Models;

namespace ClipboardPro.Services;

/// <summary>
/// 剪贴板历史的持久化仓库。
///
/// 设计要点：
/// 1. 索引（history.json）每次变更即原子写入，进程异常退出也不丢数据；
/// 2. 图片等大内容存 blobs/ 目录，索引只留引用；
/// 3. 固定项不参与条数淘汰；
/// 4. 写入前按内容指纹去重，重复内容上浮到最前而非新增一条。
/// </summary>
public class HistoryStore
{
    private readonly AppConfig _cfg;
    private readonly List<ClipItem> _items = new();

    /// <summary>供界面绑定的只读集合，顺序与 _items 一致（最新在前）。</summary>
    public ObservableCollection<ClipItem> Items { get; } = new();

    private static readonly JsonSerializerOptions IndexOpts = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public HistoryStore(AppConfig cfg)
    {
        _cfg = cfg;
        Load();
    }

    public int Count => _items.Count;
    public int PinnedCount => _items.Count(i => i.Pinned);

    #region 载入 / 保存

    private void Load()
    {
        AppPaths.EnsureDirs();
        if (!File.Exists(AppPaths.IndexPath))
        {
            AppPaths.Log("首次启动，历史索引不存在，初始化为空。");
            return;
        }

        try
        {
            var json = File.ReadAllText(AppPaths.IndexPath);
            var loaded = JsonSerializer.Deserialize<List<ClipItem>>(json, IndexOpts) ?? new List<ClipItem>();

            // 丢弃 blob 已丢失的图片条目，避免界面出现空占位。
            loaded = loaded.Where(IsUsable).ToList();
            loaded.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));

            _items.Clear();
            _items.AddRange(loaded);

            foreach (var it in _items) Items.Add(it);

            AppPaths.Log($"载入历史 {_items.Count} 条（固定 {PinnedCount} 条）。");
        }
        catch (Exception ex)
        {
            AppPaths.Log($"载入历史失败：{ex.Message}");
            // 索引损坏：备份后重新开始，避免阻塞启动。
            TryBackupCorruptIndex();
        }
    }

    private static bool IsUsable(ClipItem it)
    {
        if (it == null) return false;
        if (it.Kind != ClipKind.Image) return true;
        if (string.IsNullOrEmpty(it.BlobFile)) return false;
        return File.Exists(Path.Combine(AppPaths.BlobDir, it.BlobFile));
    }

    private static void TryBackupCorruptIndex()
    {
        try
        {
            if (File.Exists(AppPaths.IndexPath))
            {
                var bak = AppPaths.IndexPath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}";
                File.Move(AppPaths.IndexPath, bak, overwrite: true);
                AppPaths.Log($"索引损坏，已备份为 {Path.GetFileName(bak)}");
            }
        }
        catch { }
    }

    /// <summary>将当前索引原子写入磁盘。临时文件 + 覆盖移动，避免半截文件。</summary>
    private void Persist()
    {
        try
        {
            AppPaths.EnsureDirs();
            var json = JsonSerializer.Serialize(_items, IndexOpts);
            var tmp = AppPaths.IndexPath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, AppPaths.IndexPath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"写入历史失败：{ex.Message}");
        }
    }

    #endregion

    #region 新增

    /// <summary>写入一条文本。重复内容会置顶复用原条目。</summary>
    public ClipItem AddText(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length > _cfg.MaxTextLength) text = text[.._cfg.MaxTextLength];

        var hash = HashOf("T:" + text);
        var existing = _items.FirstOrDefault(i => i.Hash == hash);
        if (existing != null) return Touch(existing);

        var item = new ClipItem { Kind = ClipKind.Text, Text = text, Hash = hash };
        return Insert(item);
    }

    /// <summary>写入一组文件路径。</summary>
    public ClipItem AddFiles(IEnumerable<string> paths)
    {
        var list = paths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (list == null || list.Count == 0) return null;

        var joined = string.Join("\n", list);
        if (joined.Length > _cfg.MaxTextLength) joined = joined[.._cfg.MaxTextLength];

        var hash = HashOf("F:" + joined);
        var existing = _items.FirstOrDefault(i => i.Hash == hash);
        if (existing != null) return Touch(existing);

        var item = new ClipItem { Kind = ClipKind.Files, Text = joined, Hash = hash };
        return Insert(item);
    }

    /// <summary>写入位图。原始像素落盘为 PNG。</summary>
    public ClipItem AddImage(BitmapSource source)
    {
        if (source == null) return null;

        byte[] png;
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            png = ms.ToArray();
        }
        catch (Exception ex)
        {
            AppPaths.Log($"图片编码失败：{ex.Message}");
            return null;
        }

        if (png.LongLength > _cfg.MaxBlobBytes)
        {
            AppPaths.Log($"图片超出上限 {_cfg.MaxBlobBytes} 字节，跳过。");
            return null;
        }

        var hash = HashOf("I:" + Convert.ToHexString(SHA256.HashData(png)));
        var existing = _items.FirstOrDefault(i => i.Hash == hash);
        if (existing != null) return Touch(existing);

        var item = new ClipItem
        {
            Kind = ClipKind.Image,
            Hash = hash,
            PixelWidth = source.PixelWidth,
            PixelHeight = source.PixelHeight
        };

        var blobName = item.Id + ".png";
        try
        {
            File.WriteAllBytes(Path.Combine(AppPaths.BlobDir, blobName), png);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"图片落盘失败：{ex.Message}");
            return null;
        }

        item.BlobFile = blobName;
        return Insert(item);
    }

    private ClipItem Insert(ClipItem item)
    {
        _items.Insert(0, item);
        Items.Insert(0, item);

        EnforceLimit();
        Persist();
        return item;
    }

    /// <summary>重复内容：更新时间并置顶，不新增条目。</summary>
    private ClipItem Touch(ClipItem item)
    {
        item.CreatedAt = DateTime.Now;

        var idx = _items.IndexOf(item);
        if (idx > 0)
        {
            _items.RemoveAt(idx);
            _items.Insert(0, item);
            Items.Remove(item);
            Items.Insert(0, item);
        }
        else if (idx == 0)
        {
            // 已在首位，刷新界面时间显示即可。
            var i = Items.IndexOf(item);
            if (i >= 0) { Items.RemoveAt(i); Items.Insert(i, item); }
        }

        Persist();
        return item;
    }

    #endregion

    #region 条数上限

    /// <summary>
    /// 按 MaxEntries 淘汰最旧的非固定条目。MaxEntries=0 表示不限制。
    /// 固定项永不淘汰。
    /// </summary>
    private void EnforceLimit()
    {
        if (_cfg.MaxEntries <= 0) return;

        int removable = _items.Count(i => !i.Pinned);
        if (removable <= _cfg.MaxEntries) return;

        // 从尾部（最旧）向前删除非固定项，直到可淘汰数量达标。
        for (int i = _items.Count - 1; i >= 0 && removable > _cfg.MaxEntries; i--)
        {
            var cand = _items[i];
            if (cand.Pinned) continue;

            DeleteBlobIfAny(cand);
            _items.RemoveAt(i);
            Items.Remove(cand);
            removable--;

            AppPaths.Log($"超出上限，淘汰条目 {cand.Id}（{cand.Kind}）。");
        }
    }

    #endregion

    #region 删除 / 固定

    public void Delete(ClipItem item)
    {
        if (item == null) return;
        DeleteBlobIfAny(item);
        _items.Remove(item);
        Items.Remove(item);
        Persist();
    }

    public void SetPinned(ClipItem item, bool pinned)
    {
        if (item == null || item.Pinned == pinned) return;
        item.Pinned = pinned;
        Persist();

        // 取消固定后，可能超出上限，立即收敛。
        if (!pinned)
        {
            EnforceLimit();
            Persist();
        }
    }

    /// <summary>清空所有未固定条目，保留固定项（与系统剪贴板"全部清除"语义一致）。</summary>
    public void ClearUnpinned()
    {
        var victims = _items.Where(i => !i.Pinned).ToList();
        foreach (var v in victims)
        {
            DeleteBlobIfAny(v);
            _items.Remove(v);
            Items.Remove(v);
        }
        Persist();
        AppPaths.Log($"已清除未固定条目 {victims.Count} 条。");
    }

    /// <summary>彻底清空全部条目（含固定项）。</summary>
    public void ClearAll()
    {
        foreach (var v in _items) DeleteBlobIfAny(v);
        _items.Clear();
        Items.Clear();
        Persist();
    }

    private static void DeleteBlobIfAny(ClipItem item)
    {
        if (string.IsNullOrEmpty(item.BlobFile)) return;
        try
        {
            var p = Path.Combine(AppPaths.BlobDir, item.BlobFile);
            if (File.Exists(p)) File.Delete(p);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"删除 blob 失败：{ex.Message}");
        }
    }

    #endregion

    /// <summary>获取条目的可粘贴对象：文本 / 文件路径集合 / 位图。</summary>
    public static object GetPastePayload(ClipItem item)
    {
        if (item == null) return null;

        if (item.Kind == ClipKind.Files)
        {
            return item.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        if (item.Kind == ClipKind.Image)
        {
            var p = Path.Combine(AppPaths.BlobDir, item.BlobFile);
            if (!File.Exists(p)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(p, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        return item.Text;
    }

    private static string HashOf(string raw)
    {
        var bytes = Encoding.UTF8.GetBytes(raw);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}