using Godot;
using System;
using System.Collections.Generic;
using EasyMultiplayer.Core;
using EasyMultiplayer.Transport;

namespace EasyMultiplayer.Heartbeat;

/// <summary>
/// 心跳管理器。负责 Ping/Pong 心跳检测、RTT 计算、网络质量分级、
/// 断线检测和客户端自动重连。
/// </summary>
/// <remarks>
/// <para>
/// 心跳使用 Unreliable 通道发送，避免阻塞可靠通道。
/// 超时阈值远大于发送间隔（默认 10s vs 3s），至少容忍连续 3 次丢包。
/// </para>
/// <para>
/// 客户端断线后自动尝试重连，最大重试次数和间隔从 <see cref="EasyMultiplayerConfig"/> 读取。
/// Host 端仅等待对端重连，不主动发起。
/// </para>
/// </remarks>
public partial class HeartbeatManager : Node
{
    // ── 心跳协议通道 ──

    /// <summary>心跳 Ping/Pong 使用的内部通道编号。</summary>
    private const int HeartbeatChannel = 255;

    /// <summary>Ping 包标识字节。</summary>
    private static readonly byte[] PingPayload = { 0x01 };

    /// <summary>Pong 包标识字节。</summary>
    private static readonly byte[] PongPayload = { 0x02 };

    // ── 依赖 ──

    private ITransport? _transport;
    private EasyMultiplayerConfig _config = new();

    // ── 心跳状态 ──

    private bool _active;
    private double _heartbeatTimer;
    private double _lastPongReceived;

    // ── RTT 追踪 ──

    /// <summary>上次发送 Ping 的时间戳（毫秒）。</summary>
    private double _lastPingSentMs;

    /// <summary>当前 RTT（毫秒），-1 表示未测量。</summary>
    public double RttMs { get; private set; } = -1;

    /// <summary>当前网络质量。</summary>
    public NetQuality Quality { get; private set; } = NetQuality.Good;

    // ── 重连状态（Host 端等待） ──

    private bool _waitingReconnect;
    private double _disconnectedElapsed;

    // ── 客户端自动重连状态 ──

    private bool _clientAutoReconnecting;
    private int _reconnectAttempts;
    private double _reconnectRetryTimer;

    /// <summary>重连已等待的秒数。</summary>
    public double ReconnectElapsed => _disconnectedElapsed;

    // ── 已跟踪的对端 ──

    private readonly HashSet<int> _trackedPeers = new();

    // ── Godot 信号 ──

    /// <summary>网络质量等级变化时触发。</summary>
    [Signal]
    public delegate void NetQualityChangedEventHandler(int quality, double rttMs);

    /// <summary>对端心跳超时时触发。</summary>
    [Signal]
    public delegate void PeerTimedOutEventHandler(int peerId);

    /// <summary>对端重连成功时触发。</summary>
    [Signal]
    public delegate void PeerReconnectedEventHandler(int peerId);

    /// <summary>Host 端重连等待超时时触发。</summary>
    [Signal]
    public delegate void ReconnectTimedOutEventHandler();

    /// <summary>Client 端自动重连全部失败时触发。</summary>
    [Signal]
    public delegate void ReconnectFailedEventHandler();

    /// <summary>重连成功后需要全量同步时触发。</summary>
    [Signal]
    public delegate void FullSyncRequestedEventHandler(int peerId);

    // ── 初始化 ──

    /// <summary>
    /// 设置传输层和配置。应在使用前调用。
    /// </summary>
    /// <param name="transport">传输层实例。</param>
    /// <param name="config">配置资源。</param>
    public void Setup(ITransport transport, EasyMultiplayerConfig config)
    {
        // 清理旧的事件绑定
        if (_transport != null)
        {
            _transport.DataReceived -= OnDataReceived;
            _transport.PeerConnected -= OnTransportPeerConnected;
            _transport.PeerDisconnected -= OnTransportPeerDisconnected;
            _transport.ConnectionSucceeded -= OnTransportConnectionSucceeded;
            _transport.ConnectionFailed -= OnTransportConnectionFailed;
        }

        _transport = transport;
        _config = config;

        _transport.DataReceived += OnDataReceived;
        _transport.PeerConnected += OnTransportPeerConnected;
        _transport.PeerDisconnected += OnTransportPeerDisconnected;
        _transport.ConnectionSucceeded += OnTransportConnectionSucceeded;
        _transport.ConnectionFailed += OnTransportConnectionFailed;
    }

    // ── 公共 API ──

    /// <summary>
    /// 启动心跳检测。在连接建立后调用。
    /// </summary>
    public void Start()
    {
        _active = true;
        _heartbeatTimer = 0;
        _lastPongReceived = Time.GetUnixTimeFromSystem();
        _lastPingSentMs = 0;
        RttMs = -1;
        Quality = NetQuality.Good;
        GD.Print("[HeartbeatManager] 心跳已启动");
    }

    /// <summary>
    /// 停止心跳检测。
    /// </summary>
    public void Stop()
    {
        _active = false;
        _heartbeatTimer = 0;
        GD.Print("[HeartbeatManager] 心跳已停止");
    }

    /// <summary>
    /// 添加需要跟踪心跳的对端。
    /// </summary>
    /// <param name="peerId">对端 ID。</param>
    public void TrackPeer(int peerId)
    {
        _trackedPeers.Add(peerId);
    }

    /// <summary>
    /// 移除对端跟踪。
    /// </summary>
    /// <param name="peerId">对端 ID。</param>
    public void UntrackPeer(int peerId)
    {
        _trackedPeers.Remove(peerId);
    }

    /// <summary>
    /// 重置所有状态。
    /// </summary>
    public void Reset()
    {
        Stop();
        _waitingReconnect = false;
        _disconnectedElapsed = 0;
        _clientAutoReconnecting = false;
        _reconnectAttempts = 0;
        _reconnectRetryTimer = 0;
        _trackedPeers.Clear();
        RttMs = -1;
        Quality = NetQuality.Good;
    }

    /// <summary>
    /// 强制取消重连等待。
    /// </summary>
    public void CancelReconnect()
    {
        _waitingReconnect = false;
        _clientAutoReconnecting = false;
        _disconnectedElapsed = 0;
        _reconnectAttempts = 0;
        GD.Print("[HeartbeatManager] 重连已取消");
    }

    // ── Node 生命周期 ──

    /// <summary>
    /// 每帧处理心跳、重连计时和客户端自动重连。
    /// </summary>
    public override void _Process(double delta)
    {
        ProcessHeartbeat(delta);
        ProcessReconnectTimer(delta);
        ProcessClientAutoReconnect(delta);
    }

    /// <summary>
    /// 节点退出场景树时清理。
    /// </summary>
    public override void _ExitTree()
    {
        Reset();
        if (_transport != null)
        {
            _transport.DataReceived -= OnDataReceived;
            _transport.PeerConnected -= OnTransportPeerConnected;
            _transport.PeerDisconnected -= OnTransportPeerDisconnected;
            _transport.ConnectionSucceeded -= OnTransportConnectionSucceeded;
            _transport.ConnectionFailed -= OnTransportConnectionFailed;
        }
    }

    // ── 心跳处理 ──

    /// <summary>
    /// 处理心跳发送和超时检测。
    /// </summary>
    private void ProcessHeartbeat(double delta)
    {
        if (!_active || _transport == null || _transport.Status != TransportStatus.Connected) return;

        _heartbeatTimer += delta;
        if (_heartbeatTimer >= _config.HeartbeatInterval)
        {
            _heartbeatTimer = 0;
            SendPing();
            CheckTimeout();
        }
    }

    /// <summary>
    /// 向所有跟踪的对端发送 Ping。
    /// </summary>
    private void SendPing()
    {
        if (_transport == null || _transport.Status != TransportStatus.Connected) return;

        _lastPingSentMs = Time.GetUnixTimeFromSystem() * 1000.0;

        foreach (var peerId in _trackedPeers)
        {
            _transport.SendUnreliable(peerId, HeartbeatChannel, PingPayload);
        }
    }

    /// <summary>
    /// 检查是否有对端心跳超时。
    /// </summary>
    private void CheckTimeout()
    {
        double timeSinceLastPong = Time.GetUnixTimeFromSystem() - _lastPongReceived;
        if (timeSinceLastPong > _config.DisconnectTimeout)
        {
            GD.Print($"[HeartbeatManager] 心跳超时 ({timeSinceLastPong:F1}s)");
            HandleTimeout();
        }
    }

    /// <summary>
    /// 处理心跳超时：停止心跳，进入重连等待或启动客户端自动重连。
    /// </summary>
    private void HandleTimeout()
    {
        if (_waitingReconnect || _clientAutoReconnecting) return;

        Stop();

        // 通知所有跟踪的对端超时
        foreach (var peerId in _trackedPeers)
        {
            EmitSignal(SignalName.PeerTimedOut, peerId);
        }

        if (_transport != null && _transport.IsServer)
        {
            // Host 端：进入重连等待
            _waitingReconnect = true;
            _disconnectedElapsed = 0;
            GD.Print("[HeartbeatManager] Host 进入重连等待状态");
        }
        else
        {
            // Client 端：启动自动重连
            StartClientAutoReconnect();
        }
    }

    // ── 重连计时（Host 端） ──

    /// <summary>
    /// Host 端重连等待计时。超时后触发 ReconnectTimedOut。
    /// </summary>
    private void ProcessReconnectTimer(double delta)
    {
        if (!_waitingReconnect) return;

        _disconnectedElapsed += delta;

        if (_disconnectedElapsed >= _config.ReconnectTimeout)
        {
            GD.Print("[HeartbeatManager] 重连等待超时");
            _waitingReconnect = false;
            _disconnectedElapsed = 0;
            EmitSignal(SignalName.ReconnectTimedOut);
        }
    }

    // ── 客户端自动重连 ──

    /// <summary>
    /// 启动客户端自动重连流程。
    /// </summary>
    private void StartClientAutoReconnect()
    {
        if (_transport is not ENetTransport enetTransport)
        {
            GD.PrintErr("[HeartbeatManager] 自动重连仅支持 ENetTransport");
            EmitSignal(SignalName.ReconnectFailed);
            return;
        }

        if (string.IsNullOrEmpty(enetTransport.LastAddress))
        {
            GD.PrintErr("[HeartbeatManager] 无法自动重连：缺少上次连接地址");
            EmitSignal(SignalName.ReconnectFailed);
            return;
        }

        _clientAutoReconnecting = true;
        _reconnectAttempts = 0;
        _reconnectRetryTimer = 0; // 立即尝试第一次
        _disconnectedElapsed = 0;

        GD.Print($"[HeartbeatManager] Client 开始自动重连 (最多 {_config.MaxReconnectAttempts} 次, 间隔 {_config.ReconnectRetryInterval}s)");
    }

    /// <summary>
    /// 每帧检查是否需要执行客户端自动重连尝试。
    /// </summary>
    private void ProcessClientAutoReconnect(double delta)
    {
        if (!_clientAutoReconnecting || _transport == null) return;

        _disconnectedElapsed += delta;
        _reconnectRetryTimer -= delta;
        if (_reconnectRetryTimer > 0) return;

        _reconnectRetryTimer = _config.ReconnectRetryInterval;
        _reconnectAttempts++;

        GD.Print($"[HeartbeatManager] Client 自动重连尝试 {_reconnectAttempts}/{_config.MaxReconnectAttempts}");

        if (_transport is not ENetTransport enetTransport) return;

        // 临时解绑事件回调，防止 Disconnect() 触发的事件导致重复进入重连逻辑
        _transport.PeerDisconnected -= OnTransportPeerDisconnected;
        _transport.ConnectionFailed -= OnTransportConnectionFailed;

        // 先断开旧连接
        _transport.Disconnect();

        // 确保 Disconnect() 后状态为 Disconnected
        if (_transport.Status != TransportStatus.Disconnected)
        {
            GD.PrintErr("[HeartbeatManager] Disconnect() 后状态未重置为 Disconnected，中止重连");
            _transport.PeerDisconnected += OnTransportPeerDisconnected;
            _transport.ConnectionFailed += OnTransportConnectionFailed;
            OnClientAutoReconnectFailed();
            return;
        }

        // 重新绑定事件回调
        _transport.PeerDisconnected += OnTransportPeerDisconnected;
        _transport.ConnectionFailed += OnTransportConnectionFailed;

        // 尝试重新连接
        var error = _transport.CreateClient(enetTransport.LastAddress, enetTransport.LastPort);
        if (error != Error.Ok)
        {
            GD.PrintErr($"[HeartbeatManager] 重连创建连接失败: {error}");
            if (_reconnectAttempts >= _config.MaxReconnectAttempts)
            {
                OnClientAutoReconnectFailed();
            }
        }
        // 连接结果由 OnTransportConnectionSucceeded / OnTransportConnectionFailed 处理
    }

    /// <summary>
    /// 客户端自动重连全部失败。
    /// </summary>
    private void OnClientAutoReconnectFailed()
    {
        GD.Print("[HeartbeatManager] Client 自动重连全部失败");
        _clientAutoReconnecting = false;
        _reconnectAttempts = 0;
        _disconnectedElapsed = 0;
        _transport?.Disconnect();
        EmitSignal(SignalName.ReconnectFailed);
    }

    // ── 传输层事件处理 ──

    /// <summary>
    /// 处理收到的数据，识别心跳 Ping/Pong 包。
    /// </summary>
    private void OnDataReceived(int peerId, int channel, byte[] data)
    {
        if (channel != HeartbeatChannel || data.Length < 1) return;

        if (data[0] == PingPayload[0])
        {
            // 收到 Ping，回复 Pong
            _transport?.SendUnreliable(peerId, HeartbeatChannel, PongPayload);
        }
        else if (data[0] == PongPayload[0])
        {
            // 收到 Pong，更新 RTT
            _lastPongReceived = Time.GetUnixTimeFromSystem();

            double nowMs = _lastPongReceived * 1000.0;
            if (_lastPingSentMs > 0)
            {
                RttMs = nowMs - _lastPingSentMs;
                var oldQuality = Quality;
                Quality = RttMs switch
                {
                    < 100 => NetQuality.Good,
                    < 300 => NetQuality.Warning,
                    _ => NetQuality.Bad
                };

                if (Quality != oldQuality)
                {
                    GD.Print($"[HeartbeatManager] 网络质量变更: {oldQuality} → {Quality} (RTT={RttMs:F0}ms)");
                    EmitSignal(SignalName.NetQualityChanged, (int)Quality, RttMs);
                }
            }
        }
    }

    /// <summary>
    /// 传输层对端连接事件。处理重连成功场景。
    /// </summary>
    private void OnTransportPeerConnected(int peerId)
    {
        if (_waitingReconnect)
        {
            // Host 端：对端重连成功
            GD.Print($"[HeartbeatManager] 对端重连成功: {peerId}");
            _waitingReconnect = false;
            _disconnectedElapsed = 0;
            TrackPeer(peerId);
            Start();
            EmitSignal(SignalName.PeerReconnected, peerId);
            EmitSignal(SignalName.FullSyncRequested, peerId);
        }
    }

    /// <summary>
    /// 传输层对端断开事件。
    /// </summary>
    private void OnTransportPeerDisconnected(int peerId)
    {
        UntrackPeer(peerId);

        if (_active && _transport != null && _transport.IsServer)
        {
            // Host 端：对端断开，进入重连等待
            Stop();
            _waitingReconnect = true;
            _disconnectedElapsed = 0;
            EmitSignal(SignalName.PeerTimedOut, peerId);
            GD.Print($"[HeartbeatManager] 对端断开 ({peerId})，Host 进入重连等待");
        }
    }

    /// <summary>
    /// 客户端连接成功回调。处理自动重连成功场景。
    /// </summary>
    private void OnTransportConnectionSucceeded()
    {
        if (_clientAutoReconnecting)
        {
            GD.Print($"[HeartbeatManager] Client 自动重连成功（第 {_reconnectAttempts} 次尝试）");
            _clientAutoReconnecting = false;
            _reconnectAttempts = 0;
            _reconnectRetryTimer = 0;
            _disconnectedElapsed = 0;

            // 重新跟踪 server peer
            TrackPeer(1);
            Start();

            EmitSignal(SignalName.PeerReconnected, 1);
            EmitSignal(SignalName.FullSyncRequested, 1);
        }
    }

    /// <summary>
    /// 客户端连接失败回调。处理自动重连中的失败。
    /// </summary>
    private void OnTransportConnectionFailed()
    {
        if (_clientAutoReconnecting)
        {
            GD.Print($"[HeartbeatManager] Client 重连尝试 {_reconnectAttempts}/{_config.MaxReconnectAttempts} 失败");
            if (_reconnectAttempts >= _config.MaxReconnectAttempts)
            {
                OnClientAutoReconnectFailed();
            }
            // 否则等待下次 ProcessClientAutoReconnect 重试
        }
    }
}
