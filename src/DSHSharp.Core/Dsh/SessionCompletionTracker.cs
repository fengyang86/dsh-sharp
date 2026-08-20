namespace DSHSharp.Core.Dsh;

/// <summary>
/// 会话完成状态机：按 <c>host/session-status</c> 的 running 翻转判定"刚完成"。
/// 每个会话在 running true→false 时记一次完成；重复的 false 不重复触发，
/// 直到该会话再次进入 running 状态。
/// </summary>
public sealed class SessionCompletionTracker
{
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);

    /// <summary>
    /// 处理一次状态帧。返回 true 表示该会话"刚完成"
    /// （running true→false 翻转且此前未通知过）。
    /// </summary>
    public bool OnSessionStatus(string sessionId, bool running)
    {
        if (running)
        {
            _running.Add(sessionId);
            _completed.Remove(sessionId);
            return false;
        }

        // 仅当此前处于 running 状态时才视为完成；从未运行过的会话不触发。
        return _running.Remove(sessionId) && _completed.Add(sessionId);
    }

    /// <summary>当前正在运行（最近一次状态为 running）的会话集合。</summary>
    public IReadOnlyCollection<string> RunningSessions => _running;
}
