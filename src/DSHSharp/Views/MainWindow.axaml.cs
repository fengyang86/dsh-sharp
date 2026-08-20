using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DSHSharp.Core.Configuration;
using DSHSharp.ViewModels;

namespace DSHSharp.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private CancellationTokenSource? _notificationCts;

    /// <summary>供 XAML 运行时加载器/设计器使用。</summary>
    public MainWindow()
        : this(new AppSettings())
    {
    }

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        DataContext = new MainWindowViewModel(settings);
        Web.Source = new Uri(_settings.WebUrl);

        Closing += OnClosing;
    }

    /// <summary>显示"会话完成"等通知横幅，5 秒后自动淡出。</summary>
    public void ShowNotification(string title, string message)
    {
        NotificationTitleText.Text = title;
        NotificationMessageText.Text = message;
        NotificationBanner.IsVisible = true;
        NotificationBanner.Opacity = 0;
        NotificationBanner.Transitions ??= new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) },
        };
        NotificationBanner.Opacity = 1;

        _notificationCts?.Cancel();
        _notificationCts = new CancellationTokenSource();
        _ = AutoHideAsync(_notificationCts.Token);
    }

    private async Task AutoHideAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(HideNotification);
    }

    private void HideNotification()
    {
        _notificationCts?.Cancel();
        if (NotificationBanner.IsVisible)
        {
            NotificationBanner.Opacity = 0;
            NotificationBanner.IsVisible = false;
        }
    }

    // ---- 关闭到托盘 ----

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_settings.CloseToTray && App.Instance is { IsExiting: false })
        {
            e.Cancel = true;
            Hide();
        }
    }

    // ---- 自定义标题栏 ----

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void TitleBar_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void MinimizeButton_OnClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_OnClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e) => Close();

    // ---- 通知横幅 ----

    private void NotificationBanner_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        HideNotification();
        App.Instance?.ActivateMainWindow();
    }
}
