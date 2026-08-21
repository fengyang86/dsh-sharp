using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace DSHSharp.Views;

/// <summary>
/// 置顶 Toast 通知小窗口：独立 HWND，避免被 WebView2 原生表面遮挡。
/// 右下角显示，点击关闭，6 秒后自动关闭。
/// </summary>
public sealed class ToastWindow : Window
{
    private static readonly TimeSpan AutoCloseDelay = TimeSpan.FromSeconds(6);

    /// <param name="title">标题（如"会话已完成"）。</param>
    /// <param name="heading">主标题（会话名）。</param>
    /// <param name="preview">内容预览（回复开头，可空）。</param>
    public ToastWindow(string title, string heading, string? preview = null)
    {
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 340;
        MaxWidth = 460;
        MaxHeight = 200;

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#8E97B3")),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var headingText = new TextBlock
        {
            Text = heading,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(titleText);
        panel.Children.Add(headingText);
        if (!string.IsNullOrEmpty(preview))
        {
            var previewText = new TextBlock
            {
                Text = preview,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                LineHeight = 17,
                Foreground = new SolidColorBrush(Color.Parse("#B8C0D4")),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            panel.Children.Add(previewText);
        }

        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1E2745")),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 14),
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
