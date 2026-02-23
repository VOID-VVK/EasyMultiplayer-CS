using System;

namespace EasyMultiplayer.Transport;

/// <summary>
/// 传输层状态枚举。
/// </summary>
public enum TransportStatus
{
    /// <summary>未连接。</summary>
    Disconnected,

    /// <summary>正在连接中。</summary>
    Connecting,

    /// <summary>已连接。</summary>
    Connected
}

/// <summary>
/// 传输层抽象接口。所有网络传输实现（ENet、WebSocket 等）均需实现此接口。
/// </summary>
/// <remarks>
/// <para>
/// 设计目标：将插件与具体传输实现解耦，便于未来扩展 WebSocket、Steam Networking 等传输层。
/// </para>
/// <para>
/// <see cref="Poll"/> 方法需在每帧调用以驱动内部事件循环，
/// 这使得非 Godot 原生的传输实现也能在 <c>_Process</c> 中被驱动。
/// </para>
/// </remarks>
public interface ITransport
{
    // ── 生命周期 ──

    /// <summary>
    /// 创建主机（服务端），开始监听指定端口。
    /// </summary>
    /// <param name="port">监听端口，默认 27015。</param>
    /// <param name="maxClients">最大客户端数，默认 1。</param>
    /// <returns>操作结果，<see cref="Godot.Error.Ok"/> 表示成功。</returns>
    Godot.Error CreateHost(int port, int maxClients);

    /// <summary>
    /// 创建客户端，连接到指定主机。
    /// </summary>
    /// <param name="address">目标主机 IP 地址。</param>
    /// <param name="port">目标端口。</param>
    /// <returns>操作结果，<see cref="Godot.Error.Ok"/> 表示成功。</returns>
    Godot.Error CreateClient(string address, int port);

    /// <summary>
    /// 断开所有连接并释放资源。
    /// </summary>
    void Disconnect();

    /// <summary>
    /// 断开指定对端的连接。
    /// </summary>
    /// <param name="peerId">要断开的对端标识符。</param>
    void DisconnectPeer(int peerId);

    /// <summary>
    /// 每帧调用，驱动内部事件循环。
    /// </summary>
    void Poll();

    // ── 数据发送 ──

    /// <summary>
    /// 通过可靠通道发送数据。保证送达且按序。
    /// </summary>
    /// <param name="peerId">目标对端标识符。</param>
    /// <param name="channel">逻辑通道编号（0 = 默认）。</param>
    /// <param name="data">原始数据载荷。</param>
    void SendReliable(int peerId, int channel, byte[] data);

    /// <summary>
    /// 通过不可靠通道发送数据。不保证送达，适合高频低优先数据。
    /// </summary>
    /// <param name="peerId">目标对端标识符。</param>
    /// <param name="channel">逻辑通道编号（0 = 默认）。</param>
    /// <param name="data">原始数据载荷。</param>
    void SendUnreliable(int peerId, int channel, byte[] data);

    // ── 属性 ──

    /// <summary>当前是否为服务端（Host）。</summary>
    bool IsServer { get; }

    /// <summary>本机的唯一标识符。</summary>
    int UniqueId { get; }

    /// <summary>当前传输层状态。</summary>
    TransportStatus Status { get; }

    // ── 事件回调（由实现者触发） ──

    /// <summary>对端连接成功时触发。参数为对端 ID。</summary>
    event Action<int> PeerConnected;

    /// <summary>对端断开连接时触发。参数为对端 ID。</summary>
    event Action<int> PeerDisconnected;

    /// <summary>收到数据时触发。参数依次为：对端 ID、通道编号、数据载荷。</summary>
    event Action<int, int, byte[]> DataReceived;

    /// <summary>客户端连接主机成功时触发。</summary>
    event Action ConnectionSucceeded;

    /// <summary>客户端连接主机失败时触发。</summary>
    event Action ConnectionFailed;
}
