using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClipboardPro.Models;
using ClipboardPro.Services;

namespace ClipboardPro;

public partial class MainWindow : Window
{
    private readonly HistoryStore _store;
    private readonly AppConfig _cfg;

    private readonly List<ClipItem> _items = new();

    /// <summary>面板是否置顶显示（始终在最前）。</summary>
    private bool _panelTopmost = true;

    public MainWindow(HistoryStore store, AppConfig cfg)
    {
        _store = store;
        _cfg = cfg;

        InitializeComponent();

        ItemsHost.ItemsSource = _items;
        ThemeManager.ThemeChanged += OnThemeChanged;

        Loaded += (_, _) => PositionAtBottomCenter();
    }

    #region 显示 / 隐藏

    /// <summary>切换到前台并定位。若已可见则隐藏（模拟 Win+V 的开关行为）。</summary>
    public void ToggleVisibility()
    {
        if (IsVisible) { HidePanel(); return; }
        ShowPanel();
    }

    public void ShowPanel()
    {
        PositionPanel();
        RefreshList();

        Show();
        Activate();
        Topmost = _panelTopmost;

        // 入场动画：轻微淡入
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>定位面板：有记忆位置且仍在屏幕内则用之，否则底部居中。</summary>
    private void PositionPanel()
    {
        if (_cfg.PanelLeft >= 0 && _cfg.PanelTop >= 0 && IsPositionOnScreen(_cfg.PanelLeft, _cfg.PanelTop))
        {
            Left = _cfg.PanelLeft;
            Top = _cfg.PanelTop;
        }
        else
        {
            PositionAtBottomCenter();
        }
    }

    private static bool IsPositionOnScreen(double x, double y)
    {
        // 面板中心点与顶部都在虚拟屏幕范围内即认为可见
        var vs = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        return vs.Contains(x + 100, y + 20);
    }

    /// <summary>按住顶部空白区拖动面板，松开后记住位置。</summary>
    private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
            _cfg.PanelLeft = Left;
            _cfg.PanelTop = Top;
            _cfg.Save();
        }
        catch
        {
            // 拖动被系统取消等场景，忽略
        }
        e.Handled = true;
    }

    public void HidePanel()
    {
        if (!IsVisible) return;
        Hide();
    }

    /// <summary>定位到主屏工作区底部居中，略高于任务栏。</summary>
    private void PositionAtBottomCenter()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - 12;
    }

    private void OnThemeChanged()
    {
        // 资源字典已换，强制刷新动态资源引用
        InvalidateVisual();
        RefreshList();
    }

    #endregion

    #region 标题栏

    private void PinPanelButton_Click(object sender, RoutedEventArgs e)
    {
        _panelTopmost = !_panelTopmost;
        Topmost = _panelTopmost;
        PinPanelGlyph.Stroke = _panelTopmost
            ? (Brush)FindResource("PinGlyph")
            : (Brush)FindResource("TextSecondary");
        PinPanelGlyph.Opacity = _panelTopmost ? 1 : 0.9;
    }

    private void ClosePanelButton_Click(object sender, RoutedEventArgs e)
    {
        HidePanel();
    }

    #endregion

    #region 列表刷新

    private void RefreshList()
    {
        // 固定项优先，其余按时间倒序（_store 内部已保序，此处稳定排序）
        var list = _store.Items
            .OrderByDescending(i => i.Pinned)
            .ThenByDescending(i => i.CreatedAt)
            .ToList();

        _items.Clear();
        _items.AddRange(list);

        // 重新绑定，避免 ObservableCollection 的逐项通知
        ItemsHost.ItemsSource = null;
        ItemsHost.ItemsSource = _items;

        UpdateEmptyState();

        ClearAllButton.IsEnabled = _store.Items.Any(i => !i.Pinned);
    }

    private void UpdateEmptyState()
    {
        bool empty = _items.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ItemsScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    #endregion

    #region 卡片交互

    private void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border b)
        {
            b.Background = (Brush)FindResource("CardHoverBg");
            b.BorderBrush = (Brush)FindResource("CardBorderHover");
        }
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border b)
        {
            b.Background = (Brush)FindResource("CardBg");
            b.BorderBrush = (Brush)FindResource("CardBorder");
        }
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 点击卡片内的按钮时不触发粘贴
        if (e.OriginalSource is DependencyObject src && IsInsideButton(src)) return;

        if (sender is Border b && b.DataContext is ClipItem item)
        {
            ActivateItem(item);
        }
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

    /// <summary>选中条目：写回剪贴板，可选自动粘贴，然后隐藏面板。</summary>
    private void ActivateItem(ClipItem item)
    {
        if (!ClipboardMonitor.WriteToClipboard(item)) return;

        HidePanel();

        if (_cfg.PasteOnSelect)
        {
            // 稍等让我们的窗口失去焦点，再把焦点还给原前台窗口
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var app = Application.Current as App;
                app?.PasteToPreviousWindow();
            }), DispatcherPriority.Background);
        }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ClipItem item)
        {
            _store.SetPinned(item, !item.Pinned);
            RefreshList();
        }
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not ClipItem) return;

        // 把卡片的上下文菜单挪到按钮处弹出
        var card = FindAncestorCard(fe);
        if (card?.ContextMenu == null) return;

        card.ContextMenu.PlacementTarget = fe;
        card.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        card.ContextMenu.IsOpen = true;
    }

    private static Border FindAncestorCard(DependencyObject node)
    {
        while (node != null)
        {
            if (node is Border b && b.ContextMenu != null) return b;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private void MenuPin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && GetMenuOwner(mi) is ClipItem item)
        {
            _store.SetPinned(item, !item.Pinned);
            RefreshList();
        }
    }

    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && GetMenuOwner(mi) is ClipItem item)
        {
            _store.Delete(item);
            RefreshList();
        }
    }

    /// <summary>从 MenuItem 反查其所属卡片的数据上下文。</summary>
    private static ClipItem GetMenuOwner(MenuItem mi)
    {
        if (mi.Parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement fe)
        {
            return fe.DataContext as ClipItem;
        }
        return null;
    }

    #endregion

    #region 底部操作

    private void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        ClearUnpinnedWithConfirm();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        HidePanel();
        var app = Application.Current as App;
        app?.ShowSettings();
    }

    private void ClearUnpinnedWithConfirm()
    {
        int removable = _store.Items.Count(i => !i.Pinned);
        if (removable == 0)
        {
            MessageBox.Show(this, "没有可清除的条目（已固定的条目不会被清除）。",
                "剪贴板", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var r = MessageBox.Show(this,
            $"将清除 {removable} 条未固定的记录，已固定的条目会保留。\n此操作不可撤销，是否继续？",
            "全部清除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (r != MessageBoxResult.OK) return;

        _store.ClearUnpinned();
        RefreshList();
    }

    #endregion

    #region 键盘

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                HidePanel();
                e.Handled = true;
                break;

            case Key.Enter:
                // 回车粘贴当前第一条
                if (_items.Count > 0)
                {
                    ActivateItem(_items[0]);
                    e.Handled = true;
                }
                break;

            case Key.Delete:
                if (_items.Count > 0)
                {
                    _store.Delete(_items[0]);
                    RefreshList();
                    e.Handled = true;
                }
                break;

            case Key.Down:
                MoveFocusToList();
                e.Handled = true;
                break;
        }
    }

    private void MoveFocusToList()
    {
        var container = ItemsHost.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
        container?.Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 关闭窗口改为隐藏，保持托盘常驻
        e.Cancel = true;
        HidePanel();
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        base.OnClosed(e);
    }

    #endregion
}
