using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace DSHSharp.Views;

/// <summary>
/// 置顶 Toast 通知小窗口：独立 HWND，避免被 WebView2 原生表面遮挡。
/// 右下角显示，点击关闭，5 秒后自动关闭。
/// </summary>
public sealed class ToastWindow : Window
{
    private static readonly TimeSpan AutoCloseDelay = TimeSpan.FromSeconds(5);

    public ToastWindow(string title, string message)
    {
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 320;
        MaxWidth = 460;

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var messageText = new TextBlock
        {
            Text = message,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            Foreground = new SolidColorBrush(Color.Parse("#B8C0D4")),
        };
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(titleText);
        panel.Children.Add(messageText);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1E2745")),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12),
            Child = panel,
        };

        PointerPressed += OnPointerPressed;
        Opened += OnOpened;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e) => Close();

    private async void OnOpened(object? sender, EventArgs e)
    {
        PositionToast();
        try
        {
            await Task.Delay(AutoCloseDelay);
        }
        catch (Exception)
        {
            // 窗口已关闭：忽略。
        }

        if (IsVisible)
        {
            Close();
        }
    }

    /// <summary>定位到主屏工作区右下角。</summary>
    private void PositionToast()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        Position = new PixelPoint(
            area.Right - (int)Width - 16,
            area.Bottom - (int)Height - 16);
    }
}
