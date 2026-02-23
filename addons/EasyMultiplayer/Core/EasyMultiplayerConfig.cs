using Godot;

namespace EasyMultiplayer.Core;

/// <summary>
/// EasyMultiplayer 配置资源类。
/// 所有可配置参数通过 <see cref="Resource"/> 暴露，支持 Inspector 编辑和 .tres 文件持久化。
/// </summary>
[GlobalClass]
public partial class EasyMultiplayerConfig : Resource
{
    // ── 连接 ──

    /// <summary>ENet 监听/连接端口。</summary>
    [Export] public int Port { get; set; } = 27015;

    /// <summary>最大客户端数。</summary>
    [Export] public int MaxClients { get; set; } = 1;

    // ── 心跳 ──

    /// <summary>心跳 Ping 发送间隔（秒）。</summary>
    [Export] public float HeartbeatInterval { get; set; } = 3.0f;

    /// <summary>判定对端断线的超时阈值（秒）。</summary>
    [Export] public float DisconnectTimeout { get; set; } = 10.0f;

    // ── 重连 ──

    /// <summary>Host 端等待对端重连的上限时间（秒）。</summary>
    [Export] public float ReconnectTimeout { get; set; } = 30.0f;

    /// <summary>Client 端最大重连尝试次数。</summary>
    [Export] public int MaxReconnectAttempts { get; set; } = 20;

    /// <summary>Client 端每次重连尝试的间隔（秒）。</summary>
    [Export] public float ReconnectRetryInterval { get; set; } = 3.0f;

    // ── 消息 ──

    /// <summary>消息通道每通道最小发送间隔（毫秒），0 表示不限制。</summary>
    [Export] public double RpcMinIntervalMs { get; set; } = 100.0;

    // ── 发现 ──

    /// <summary>UDP 广播端口。</summary>
    [Export] public int BroadcastPort { get; set; } = 27016;

    /// <summary>广播发送间隔（秒）。</summary>
    [Export] public float BroadcastInterval { get; set; } = 1.0f;

    /// <summary>房间超时移除阈值（秒）。超过此时间未收到广播的房间将被移除。</summary>
    [Export] public double RoomTimeout { get; set; } = 5.0;
}
