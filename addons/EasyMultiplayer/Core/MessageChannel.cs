using Godot;
using System;
using System.Collections.Generic;
using System.Text;
using EasyMultiplayer.Transport;

namespace EasyMultiplayer.Core;

/// <summary>
/// 通用消息通道。提供统一的消息收发接口，替代散落的业务 RPC。
/// </summary>
/// <remarks>
/// <para>
/// 插件不解析消息内容，只负责投递。使用者通过 string channel 标识逻辑通道
/// （如 <c>"move"</c>、<c>"chat"</c>、<c>"sync"</c>），自行定义消息格式。
/// </para>
/// <para>
/// 支持 Reliable（可靠，保证送达且按序）和 Unreliable（不可靠，适合高频低优先数据）两种模式。
/// 内置 RPC 频率限制，超过频率的消息会被静默丢弃并打印警告日志。
/// </para>
/// </remarks>
public partial class MessageChannel : Node
{
    /// <summary>消息通道使用的内部传输通道编号（避免与心跳通道 255 冲突）。</summary>
    private const int TransportChannel = 0;

    // ── 依赖 ──

    private ITransport? _transport;

    // ── 频率限制 ──

    /// <summary>
    /// 每通道最小发送间隔（毫秒），0 表示不限制。
    /// </summary>
    public double RpcMinIntervalMs { get; set; } = 100.0;

    /// <summary>记录每个逻辑通道上次发送时间（毫秒）。</summary>
    private readonly Dictionary<string, double> _channelLastSendTime = new();

    // ── Godot 信号 ──

    /// <summary>
    /// 收到消息时触发。
    /// </summary>
    /// <param name="peerId">发送方对端 ID。</param>
    /// <param name="channel">逻辑通道标识。</param>
    /// <param name="data">消息载荷。</param>
    [Signal]
    public delegate void MessageReceivedEventHandler(int peerId, string channel, byte[] data);

    // ── 初始化 ──

    /// <summary>
    /// 设置传输层引用。应在使用前调用。
    /// </summary>
    /// <param name="transport">传输层实例。</param>
    public void SetTransport(ITransport transport)
    {
        if (_transport != null)
        {
            _transport.DataReceived -= OnDataReceived;
        }

        _transport = transport;
        _transport.DataReceived += OnDataReceived;
    }

    // ── 公共 API ──

    /// <summary>
    /// 发送可靠消息（byte[] 载荷）。保证送达且按序。
    /// </summary>
    /// <param name="peerId">目标对端 ID。</param>
    /// <param name="channel">逻辑通道标识。</param>
    /// <param name="data">消息载荷。</param>
    public void SendReliable(int peerId, string channel, byte[] data)
    {
        if (!CheckRateLimit(channel)) return;
        var packet = PackMessage(channel, data);
        _transport?.SendReliable(peerId, TransportChannel, packet);
    }

    /// <summary>
    /// 发送可靠消息（string 载荷，自动 UTF-8 编码）。
    /// </summary>
    /// <param name="peerId">目标对端 ID。</param>
    /// <param name="channel">逻辑通道标识。</param>
    /// <param name="data">消息字符串。</param>
    public void SendReliable(int peerId, string channel, string data)
    {
        SendReliable(peerId, channel, Encoding.UTF8.GetBytes(data));
    }

    /// <summary>
    /// 发送不可靠消息。不保证送达，适合高频低优先数据。
    /// </summary>
    /// <param name="peerId">目标对端 ID。</param>
    /// <param name="channel">逻辑通道标识。</param>
    /// <param name="data">消息载荷。</param>
    public void SendUnreliable(int peerId, string channel, byte[] data)
    {
        if (!CheckRateLimit(channel)) return;
        var packet = PackMessage(channel, data);
        _transport?.SendUnreliable(peerId, TransportChannel, packet);
    }

    /// <summary>
    /// 广播消息给所有已连接对端。
    /// </summary>
    /// <param name="channel">逻辑通道标识。</param>
    /// <param name="data">消息载荷。</param>
    /// <param name="reliable">是否使用可靠传输，默认 true。</param>
    public void Broadcast(string channel, byte[] data, bool reliable = true)
    {
        if (!CheckRateLimit(channel)) return;
        var packet = PackMessage(channel, data);

        // peerId = 0 表示广播给所有对端
        if (reliable)
        {
            _transport?.SendReliable(0, TransportChannel, packet);
        }
        else
        {
            _transport?.SendUnreliable(0, TransportChannel, packet);
        }
    }

    /// <summary>
    /// 重置频率限制计时器。
    /// </summary>
    public void ResetRateLimits()
    {
        _channelLastSendTime.Clear();
    }

    // ── Node 生命周期 ──

    /// <summary>
    /// 节点退出场景树时清理。
    /// </summary>
    public override void _ExitTree()
    {
        if (_transport != null)
        {
            _transport.DataReceived -= OnDataReceived;
        }
        _channelLastSendTime.Clear();
    }

    // ── 内部逻辑 ──

    /// <summary>
    /// 检查指定通道是否超过频率限制。
    /// </summary>
    /// <param name="channel">逻辑通道标识。</param>
    /// <returns>true 表示放行，false 表示被限制。</returns>
    private bool CheckRateLimit(string channel)
    {
        if (RpcMinIntervalMs <= 0) return true;

        double nowMs = Time.GetUnixTimeFromSystem() * 1000.0;
        if (_channelLastSendTime.TryGetValue(channel, out double lastMs))
        {
            if (nowMs - lastMs < RpcMinIntervalMs)
            {
                GD.Print($"[MessageChannel] 频率限制: 通道 \"{channel}\" 被拒绝 (间隔 {nowMs - lastMs:F0}ms < {RpcMinIntervalMs}ms)");
                return false;
            }
        }
        _channelLastSendTime[channel] = nowMs;
        return true;
    }

    /// <summary>
    /// 将逻辑通道名和数据打包为传输包。
    /// 格式：[channelNameLength:2bytes][channelName:UTF8][data]
    /// </summary>
    /// <param name="channel">逻辑通道标识。</param>
    /// <param name="data">消息载荷。</param>
    /// <returns>打包后的字节数组。</returns>
    private static byte[] PackMessage(string channel, byte[] data)
    {
        var channelBytes = Encoding.UTF8.GetBytes(channel);
        var channelLen = (ushort)channelBytes.Length;

        var packet = new byte[2 + channelBytes.Length + data.Length];
        BitConverter.GetBytes(channelLen).CopyTo(packet, 0);
        channelBytes.CopyTo(packet, 2);
        data.CopyTo(packet, 2 + channelBytes.Length);

        return packet;
    }

    /// <summary>
    /// 从传输包中解析逻辑通道名和数据。
    /// </summary>
    /// <param name="packet">传输包。</param>
    /// <returns>逻辑通道标识和消息载荷，解析失败返回 null。</returns>
    private static (string channel, byte[] data)? UnpackMessage(byte[] packet)
    {
        if (packet.Length < 2) return null;

        var channelLen = BitConverter.ToUInt16(packet, 0);
        if (packet.Length < 2 + channelLen) return null;

        var channel = Encoding.UTF8.GetString(packet, 2, channelLen);
        var dataOffset = 2 + channelLen;
        var data = new byte[packet.Length - dataOffset];
        Array.Copy(packet, dataOffset, data, 0, data.Length);

        return (channel, data);
    }

    /// <summary>
    /// 传输层数据接收回调。解析消息并触发 MessageReceived 信号。
    /// </summary>
    private void OnDataReceived(int peerId, int transportChannel, byte[] rawData)
    {
        // 只处理消息通道的数据，忽略心跳等其他通道
        if (transportChannel != TransportChannel) return;

        var result = UnpackMessage(rawData);
        if (result == null)
        {
            GD.PrintErr($"[MessageChannel] 收到无法解析的消息包 (peerId={peerId}, len={rawData.Length})");
            return;
        }

        var (channel, data) = result.Value;
        EmitSignal(SignalName.MessageReceived, peerId, channel, data);
    }
}
