// 本项目同时启用 WPF 与 WinForms（托盘图标需要 NotifyIcon）。
// SDK 会为二者注入全局 using，导致同名类型（Application / Clipboard /
// MessageBox / Brush / Button / MouseEventArgs 等）二义性。
// 这里用全局别名统一固定为 WPF 版本；WinForms 类型在代码中显式全名引用。

global using Application = System.Windows.Application;
global using Clipboard = System.Windows.Clipboard;
global using MessageBox = System.Windows.MessageBox;
global using MessageBoxButton = System.Windows.MessageBoxButton;
global using MessageBoxImage = System.Windows.MessageBoxImage;
global using MessageBoxResult = System.Windows.MessageBoxResult;

global using Brush = System.Windows.Media.Brush;
global using Color = System.Windows.Media.Color;
global using Pen = System.Windows.Media.Pen;
global using FontFamily = System.Windows.Media.FontFamily;

global using Button = System.Windows.Controls.Button;
global using CheckBox = System.Windows.Controls.CheckBox;
global using ContextMenu = System.Windows.Controls.ContextMenu;
global using MenuItem = System.Windows.Controls.MenuItem;
global using Separator = System.Windows.Controls.Separator;
global using TextBox = System.Windows.Controls.TextBox;
global using TextBlock = System.Windows.Controls.TextBlock;
global using Image = System.Windows.Controls.Image;
global using Border = System.Windows.Controls.Border;
global using Grid = System.Windows.Controls.Grid;
global using StackPanel = System.Windows.Controls.StackPanel;
global using ScrollViewer = System.Windows.Controls.ScrollViewer;
global using ItemsControl = System.Windows.Controls.ItemsControl;
global using Slider = System.Windows.Controls.Slider;
global using ToolTip = System.Windows.Controls.ToolTip;
global using Panel = System.Windows.Controls.Panel;
global using ProgressBar = System.Windows.Controls.ProgressBar;

global using MouseEventArgs = System.Windows.Input.MouseEventArgs;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
global using Key = System.Windows.Input.Key;
global using Keyboard = System.Windows.Input.Keyboard;
global using MouseButton = System.Windows.Input.MouseButton;
global using Cursor = System.Windows.Input.Cursor;
global using Cursors = System.Windows.Input.Cursors;