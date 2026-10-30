using System;
using System.Windows;
using Microsoft.Win32;

namespace ClipboardPro.Services;

/// <summary>主题管理：根据配置与系统设置选择浅色 / 深色资源字典。</summary>
public static class ThemeManager
{
    private const string LightPath = "Themes/Light.xaml";
    private const string DarkPath = "Themes/Dark.xaml";

    /// <summary>当前是否深色。</summary>
    public static bool IsDark { get; private set; }

    /// <summary>主题变化事件，供已打开窗口重绘。</summary>
    public static event Action ThemeChanged;

    /// <summary>读取系统"应用模式"：0=深色，1=浅色。</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = key?.GetValue("AppsUseLightTheme");
            if (v is int i) return i == 0;
        }
        catch { }
        return false;
    }

    public static void Apply(string theme)
    {
        bool dark = theme switch
        {
            "Dark" => true,
            "Light" => false,
            _ => SystemPrefersDark()
        };

        IsDark = dark;
        SwapColors(dark ? DarkPath : LightPath);
        ThemeChanged?.Invoke();
    }

    private static void SwapColors(string path)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        ResourceDictionary colors = null;

        // 颜色字典固定放在第一位，其余为控件样式与转换器
        if (dicts.Count > 0 && IsColorDict(dicts[0]))
        {
            colors = dicts[0];
        }

        var fresh = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{path}", UriKind.Absolute)
        };

        if (colors != null) dicts.RemoveAt(0);
        dicts.Insert(0, fresh);
    }

    /// <summary>判断某个字典是否为颜色字典（含 PanelBg 键）。</summary>
    private static bool IsColorDict(ResourceDictionary d)
    {
        try
        {
            return d.Contains("PanelBg");
        }
        catch
        {
            return false;
        }
    }
}