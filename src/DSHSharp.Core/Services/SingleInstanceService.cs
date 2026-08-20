using System.Threading;

namespace DSHSharp.Core.Services;

/// <summary>
/// 基于命名 Mutex 的单实例保证：
/// 首实例持有 Mutex 并后台监听激活事件；第二实例检测到已被占用时
/// 通过命名事件通知首实例唤起主窗口，然后自行退出。
/// 命名对象使用 <c>Local\</c> 前缀，限定在当前登录会话内。
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\DSHSharp.SingleInstance";
    private const string ActivateEventName = @"Local\DSHSharp.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private Thread? _listenerThread;
    private bool _disposed;

    /// <summary>当前进程是否为第一个实例。</summary>
    public bool IsFirstInstance { get; }

    public SingleInstanceService()
    {
        _mutex = new Mutex(initiallyOwned: false, MutexName, out bool createdNew);
        IsFirstInstance = createdNew;
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
    }

    /// <summary>通知首实例唤起主窗口（由第二实例调用）。</summary>
    public void NotifyFirstInstance()
    {
        try
        {
            _activateEvent.Set();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ObjectDisposedException)
        {
            // 首实例可能已退出或事件不可用：忽略。
        }
    }

    /// <summary>首实例：后台线程监听激活信号，收到后在调用方线程执行回调。</summary>
    public void StartListener(Action onActivated)
    {
        ArgumentNullException.ThrowIfNull(onActivated);

        _listenerThread = new Thread(() =>
        {
            while (!_disposed)
            {
                try
                {
                    _activateEvent.WaitOne();
                    onActivated();
                }
                catch (ThreadInterruptedException)
                {
                    break;
                }
                catch
                {
                    // 事件句柄被释放（应用退出）或瞬时异常：结束监听。
                    break;
                }
            }
        })
        {
            IsBackground = true,
            Name = "DSHSharp.ActivateListener",
        };
        _listenerThread.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listenerThread?.Interrupt();
        _listenerThread?.Join(TimeSpan.FromMilliseconds(500));
        _mutex.Dispose();
        _activateEvent.Dispose();
    }
}
