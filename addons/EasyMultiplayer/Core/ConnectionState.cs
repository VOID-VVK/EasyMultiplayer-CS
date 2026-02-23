namespace EasyMultiplayer.Core;

/// <summary>
/// 连接状态枚举，表示 EasyMultiplayer 的连接生命周期阶段。
/// </summary>
/// <remarks>
/// 状态转换规则：
/// <list type="bullet">
///   <item>Disconnected → Hosting：调用 Host()</item>
///   <item>Disconnected → Joining：调用 Join()</item>
///   <item>Hosting → Connected：收到 PeerConnected</item>
///   <item>Joining → Connected：收到 ConnectionSucceeded</item>
///   <item>Connected → Reconnecting：心跳超时或 PeerDisconnected（非主动退出）</item>
///   <item>Reconnecting → Connected：对端重新连接成功</item>
///   <item>Reconnecting → Disconnected：重连超时或用户取消</item>
///   <item>Any → Disconnected：调用 Disconnect()</item>
/// </list>
/// </remarks>
public enum ConnectionState
{
    /// <summary>初始状态或已断开连接。</summary>
    Disconnected,

    /// <summary>作为主机等待客户端连接。</summary>
    Hosting,

    /// <summary>客户端正在连接到主机。</summary>
    Joining,

    /// <summary>已连接，双方在线。</summary>
    Connected,

    /// <summary>对端断线，等待重连中。</summary>
    Reconnecting
}

/// <summary>
/// 网络质量分级枚举，基于 RTT（往返时延）评估。
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Good：RTT &lt; 100ms</item>
///   <item>Warning：100ms ≤ RTT &lt; 300ms</item>
///   <item>Bad：RTT ≥ 300ms 或断线</item>
/// </list>
/// </remarks>
public enum NetQuality
{
    /// <summary>网络质量良好，RTT &lt; 100ms。</summary>
    Good,

    /// <summary>网络质量一般，100ms ≤ RTT &lt; 300ms。</summary>
    Warning,

    /// <summary>网络质量差，RTT ≥ 300ms 或断线。</summary>
    Bad
}
