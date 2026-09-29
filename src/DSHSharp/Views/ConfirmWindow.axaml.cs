using Avalonia.Interactivity;
using Avalonia.Controls;

namespace DSHSharp.Views;

/// <summary>
/// 通用确认对话框（模态）：标题 + 说明 + 取消/确认。
/// 用于更新/重启前中断运行中任务（agent 会话）的排水确认。
/// </summary>
public partial class ConfirmWindow : Window
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConfirmWindow()
    {
        InitializeComponent();
    }

    private ConfirmWindow(string title, string message, string confirmText) : this()
    {
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    /// <summary>弹出确认框；owner 缺失时退化为直接确认（无法交互的场景不阻塞流程）。</summary>
    public static async Task<bool> AskAsync(Window? owner, string title, string message, string confirmText = "确认")
    {
        if (owner is null)
        {
            return true;
        }

        var dialog = new ConfirmWindow(title, message, confirmText) { Title = title };
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Closed += (_, _) => closed.TrySetResult(true);
        await dialog.ShowDialog(owner);
        await closed.Task; // ShowDialog 完成即按钮已点击/窗口已关，结果已就位。
        return await dialog._result.Task;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        _result.TrySetResult(true);
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _result.TrySetResult(false);
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 任意关闭路径（含 Alt+F4）都视为未确认。
        _result.TrySetResult(false);
        base.OnClosing(e);
    }
}
