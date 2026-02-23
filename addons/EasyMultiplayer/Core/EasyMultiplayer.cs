using Godot;
using System;
using System.Collections.Generic;
using System.Text;
using EasyMultiplayer.Discovery;
using EasyMultiplayer.Heartbeat;
using EasyMultiplayer.Room;
using EasyMultiplayer.Transport;

namespace EasyMultiplayer.Core;

/// <summary>
/// EasyMultiplayer Autoload 单例。统一 API 入口，组合所有模块。
/// </summary>
/// <remarks>
/// <para>
/// 管理连接生命周期（ConnectionState 状态机）、版本校验、主动退出通知。
/// 组合 ITransport、IDiscovery、HeartbeatManager、MessageChannel、RoomHost、RoomClient。
/// </para>
/// <para>
/// 所有事件通过 Godot Signal 暴露，不依赖外部 EventBus。
/// 使用者通过此单例访问所有网络功能。
/// </para>
/// </remarks>
public partial class EasyMultiplayer : Node
{
    // ── 版本校验内部通道 ──

    /// <summary>版本校验使用的内部消息通道。</summary>
    private const string VersionChannel = "__version";

    /// <summary>版本校验消息前缀。</summary>
    private const string MsgVersion = "ver:";

    /// <summary>版本校验通过消息前缀。</summary>
    private const string MsgVersionOk = "ver_ok:";

    /// <summary>版本不匹配消息前缀。</summary>
    private const string MsgVersionMismatch = "ver_mismatch:";

    // ── 主动退出内部通道 ──

    /// <summary>主动退出使用的内部消息通道。</summary>
    private const string QuitChannel = "__quit";

    /// <summary>主动退出消息前缀。</summary>
    private const string MsgQuit = "quit:";

    // ── 配置 ──

    /// <summary>
    /// 插件配置资源。可在 Inspector 中编辑或代码中动态修改。
    /// </summary>
    [Export]
    public EasyMultiplayerConfig Config { get; set; } = new();

    /// <summary>
    /// 使用者设置的游戏版本号，连接时自动交换校验。
    /// </summary>
    public string GameVersion { get; set; } = "1.0.0";

    // ── 模块实例 ──

    private ENetTransport _transport = null!;
    private UdpBroadcastDiscovery _discovery = null!;
    private HeartbeatManager _heartbeat = null!;
    private MessageChannel _messageChannel = null!;
    private RoomHost _roomHost = null!;
    private RoomClient _roomClient = null!;

    /// <summary>传输层实例（ITransport 接口）。</summary>
    public ITransport Transport => _transport;

    /// <summary>发现层实例（IDiscovery 接口）。</summary>
    public IDiscovery Discovery => _discovery;

    /// <summary>心跳管理器。</summary>
    public HeartbeatManager Heartbeat => _heartbeat;

    /// <summary>消息通道。</summary>
    public MessageChannel MessageChannel => _messageChannel;

    /// <summary>房间主机。</summary>
    public RoomHost RoomHost => _roomHost;

    /// <summary>房间客户端。</summary>
    public RoomClient RoomClient => _roomClient;

    // ── 连接状态 ──

    private ConnectionState _state = ConnectionState.Disconnected;

    /// <summary>当前连接状态。</summary>
    public ConnectionState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            var old = _state;
            _state = value;
            GD.Print($"[EasyMultiplayer] 连接状态: {old} → {value}");
            EmitSignal(SignalName.StateChanged, (int)old, (int)value);
        }
    }

    /// <summary>当前连接的对端 peer ID 集合。</summary>
    private readonly HashSet<int> _connectedPeers = new();

    /// <summary>标记对端是否主动退出（用于区分主动退出和意外断线）。</summary>
    private readonly HashSet<int> _gracefullyQuitPeers = new();

    /// <summary>Joining 超时计时器（秒）。</summary>
    private double _joiningTimer;

    /// <summary>Joining 超时阈值（秒）。</summary>
    private const double JoiningTimeout = 10.0;

    /// <summary>版本校验超时计时器（秒）。</summary>
    private double _versionCheckTimer;

    /// <summary>版本校验超时阈值（秒）。</summary>
    private const double VersionCheckTimeout = 5.0;

    /// <summary>是否正在等待版本校验结果。</summary>
    private bool _waitingVersionCheck;

    /// <summary>是否为服务端。</summary>
    public bool IsServer => _transport.IsServer;

    /// <summary>本机唯一标识符。</summary>
    public int UniqueId => _transport.UniqueId;

    /// <summary>是否已连接（网络层）。Host 端处于 Hosting 状态也视为已连接。</summary>
    public bool IsNetworkConnected => _state == ConnectionState.Connected || _state == ConnectionState.Hosting;

    /// <summary>是否正在重连。</summary>
    public bool IsReconnecting => _state == ConnectionState.Reconnecting;

    /// <summary>已连接的对端 ID 列表。</summary>
    public IReadOnlyCollection<int> ConnectedPeers => _connectedPeers;

    // ── Godot 信号：连接模块 ──

    /// <summary>连接状态发生转换时触发。</summary>
    [Signal]
    public delegate void StateChangedEventHandler(int oldState, int newState);

    /// <summary>对端连接成功时触发。</summary>
    [Signal]
    public delegate void PeerJoinedEventHandler(int peerId);

    /// <summary>对端断开连接时触发。</summary>
    [Signal]
    public delegate void PeerLeftEventHandler(int peerId);

    /// <summary>Client 连接 Host 成功时触发。</summary>
    [Signal]
    public delegate void ConnectionSucceededEventHandler();

    /// <summary>Client 连接 Host 失败时触发。</summary>
    [Signal]
    public delegate void ConnectionFailedEventHandler();

    /// <summary>版本校验通过时触发。</summary>
    [Signal]
    public delegate void VersionVerifiedEventHandler(string remoteVersion);

    /// <summary>版本不匹配时触发。</summary>
    [Signal]
    public delegate void VersionMismatchEventHandler(string localVersion, string remoteVersion);

    /// <summary>对端主动退出时触发。</summary>
    [Signal]
    public delegate void PeerGracefulQuitEventHandler(int peerId, string reason);

    /// <summary>重连成功后需要全量同步时触发。</summary>
    [Signal]
    public delegate void FullSyncRequestedEventHandler(int peerId);

    // ── Node 生命周期 ──

    /// <summary>
    /// 节点就绪时初始化所有模块。
    /// </summary>
    public override void _Ready()
    {
        // 创建传输层
        _transport = new ENetTransport();
        _transport.Initialize(GetTree());

        // 创建发现层
        _discovery = new UdpBroadcastDiscovery();
        _discovery.SetConfig(Config);
        AddChild(_discovery);
        // 提前在后台线程预加载本机 IP，避免 StartListening 首次调用时阻塞主线程
        _discovery.PreloadLocalIps();

        // 创建心跳管理器
        _heartbeat = new HeartbeatManager();
        _heartbeat.Setup(_transport, Config);
        AddChild(_heartbeat);

        // 创建消息通道
        _messageChannel = new MessageChannel();
        _messageChannel.SetTransport(_transport);
        _messageChannel.RpcMinIntervalMs = Config.RpcMinIntervalMs;
        AddChild(_messageChannel);

        // 创建房间模块
        _roomHost = new RoomHost();
        _roomHost.Setup(_transport, _discovery, _messageChannel, Config, GameVersion);
        AddChild(_roomHost);

        _roomClient = new RoomClient();
        _roomClient.Setup(_transport, _discovery, _messageChannel);
        AddChild(_roomClient);

        // 绑定传输层事件
        _transport.PeerConnected += OnTransportPeerConnected;
        _transport.PeerDisconnected += OnTransportPeerDisconnected;
        _transport.ConnectionSucceeded += OnTransportConnectionSucceeded;
        _transport.ConnectionFailed += OnTransportConnectionFailed;

        // 绑定心跳事件
        _heartbeat.PeerTimedOut += OnHeartbeatPeerTimedOut;
        _heartbeat.PeerReconnected += OnHeartbeatPeerReconnected;
        _heartbeat.ReconnectTimedOut += OnHeartbeatReconnectTimedOut;
        _heartbeat.ReconnectFailed += OnHeartbeatReconnectFailed;
        _heartbeat.FullSyncRequested += OnHeartbeatFullSyncRequested;

        // 绑定消息通道事件（处理版本校验和主动退出）
        _messageChannel.MessageReceived += OnInternalMessageReceived;

        // 绑定房间模块状态事件，同步 EasyMultiplayer 连接状态
        _roomHost.RoomStateChanged += OnRoomHostStateChanged;
        _roomClient.ClientStateChanged += OnRoomClientStateChanged;

        // 确保 EasyMultiplayer 的 _Process 在 SceneMultiplayer 之前执行，
        // 先消费所有数据包，防止 SceneMultiplayer 抢先消费导致 Poll() 收不到数据。
        ProcessPriority = int.MinValue;

        GD.Print("[EasyMultiplayer] 初始化完成");
    }

    /// <summary>
    /// 每帧驱动传输层事件循环和超时检查。
    /// </summary>
    public override void _Process(double delta)
    {
        _transport.Poll();

        // Joining 超时检查
        if (_state == ConnectionState.Joining)
        {
            _joiningTimer += delta;
            if (_joiningTimer >= JoiningTimeout)
            {
                GD.PrintErr("[EasyMultiplayer] Joining 超时 (10s)");
                _joiningTimer = 0;
                Disconnect();
                EmitSignal(SignalName.ConnectionFailed);
            }
        }

        // 版本校验超时检查
        if (_waitingVersionCheck)
        {
            _versionCheckTimer += delta;
            if (_versionCheckTimer >= VersionCheckTimeout)
            {
                GD.PrintErr("[EasyMultiplayer] 版本校验超时 (5s)");
                _waitingVersionCheck = false;
                _versionCheckTimer = 0;
                Disconnect();
                EmitSignal(SignalName.ConnectionFailed);
            }
        }
    }

    /// <summary>
    /// 节点退出场景树时清理所有资源。
    /// </summary>
    public override void _ExitTree()
    {
        Disconnect();

        _transport.PeerConnected -= OnTransportPeerConnected;
        _transport.PeerDisconnected -= OnTransportPeerDisconnected;
        _transport.ConnectionSucceeded -= OnTransportConnectionSucceeded;
        _transport.ConnectionFailed -= OnTransportConnectionFailed;

        _heartbeat.PeerTimedOut -= OnHeartbeatPeerTimedOut;
        _heartbeat.PeerReconnected -= OnHeartbeatPeerReconnected;
        _heartbeat.ReconnectTimedOut -= OnHeartbeatReconnectTimedOut;
        _heartbeat.ReconnectFailed -= OnHeartbeatReconnectFailed;
        _heartbeat.FullSyncRequested -= OnHeartbeatFullSyncRequested;

        _messageChannel.MessageReceived -= OnInternalMessageReceived;

        _roomHost.RoomStateChanged -= OnRoomHostStateChanged;
        _roomClient.ClientStateChanged -= OnRoomClientStateChanged;

        _transport.Cleanup();
    }

    // ── 公共 API：连接管理 ──

    /// <summary>
    /// 作为主机开始监听。
    /// </summary>
    /// <param name="port">监听端口，默认从配置读取。</param>
    /// <param name="maxClients">最大客户端数，默认从配置读取。</param>
    /// <returns>操作结果。</returns>
    public Error Host(int port = -1, int maxClients = -1)
    {
        if (_state != ConnectionState.Disconnected)
        {
            GD.PrintErr("[EasyMultiplayer] 无法创建主机：当前状态不是 Disconnected");
            return Error.AlreadyInUse;
        }

        var actualPort = port > 0 ? port : Config.Port;
        var actualMaxClients = maxClients > 0 ? maxClients : Config.MaxClients;

        var error = _transport.CreateHost(actualPort, actualMaxClients);
        if (error != Error.Ok) return error;

        State = ConnectionState.Hosting;
        return Error.Ok;
    }

    /// <summary>
    /// 作为客户端连接到主机。
    /// </summary>
    /// <param name="address">主机 IP 地址。</param>
    /// <param name="port">主机端口，默认从配置读取。</param>
    /// <returns>操作结果。</returns>
    public Error Join(string address, int port = -1)
    {
        if (_state != ConnectionState.Disconnected)
        {
            GD.PrintErr("[EasyMultiplayer] 无法连接：当前状态不是 Disconnected");
            return Error.AlreadyInUse;
        }

        var actualPort = port > 0 ? port : Config.Port;

        var error = _transport.CreateClient(address, actualPort);
        if (error != Error.Ok) return error;

        State = ConnectionState.Joining;
        _joiningTimer = 0;
        return Error.Ok;
    }

    /// <summary>
    /// 断开连接并重置所有状态。
    /// </summary>
    public void Disconnect()
    {
        _heartbeat.Reset();
        _discovery.StopBroadcast();
        _discovery.StopListening();
        _transport.Disconnect();
        _messageChannel.ResetRateLimits();

        _connectedPeers.Clear();
        _gracefullyQuitPeers.Clear();
        _joiningTimer = 0;
        _waitingVersionCheck = false;
        _versionCheckTimer = 0;

        State = ConnectionState.Disconnected;
    }

    // ── 公共 API：房间快捷方法 ──

    /// <summary>
    /// 创建房间（快捷方法，内部调用 RoomHost.CreateRoom）。
    /// </summary>
    /// <param name="roomName">房间名称。</param>
    /// <param name="gameType">游戏类型。</param>
    /// <param name="port">端口，默认从配置读取。</param>
    /// <returns>操作结果。</returns>
    public Error CreateRoom(string roomName, string gameType, int port = -1)
    {
        return _roomHost.CreateRoom(roomName, gameType, port);
    }

    /// <summary>
    /// 加入房间（快捷方法，内部调用 RoomClient.JoinRoom）。
    /// </summary>
    /// <param name="hostIp">主机 IP。</param>
    /// <param name="port">端口。</param>
    /// <returns>操作结果。</returns>
    public Error JoinRoom(string hostIp, int port = -1)
    {
        var actualPort = port > 0 ? port : Config.Port;
        return _roomClient.JoinRoom(hostIp, actualPort);
    }

    // ── 公共 API：消息 ──

    /// <summary>
    /// 发送可靠消息（快捷方法）。
    /// </summary>
    /// <param name="peerId">目标对端 ID。</param>
    /// <param name="channel">逻辑通道标识。</param>
    /// <param name="data">消息载荷。</param>
    public void SendMessage(int peerId, string channel, byte[] data)
    {
        _messageChannel.SendReliable(peerId, channel, data);
    }

    /// <summary>
    /// 发送可靠消息（string 重载）。
    /// </summary>
    /// <param name="peerId">目标对端 ID。</param>
    /// <param name="channel">逻辑通道标识。</param>
    /// <param name="data">消息字符串。</param>
    public void SendMessage(int peerId, string channel, string data)
    {
        _messageChannel.SendReliable(peerId, channel, data);
    }

    /// <summary>
    /// 广播消息给所有对端（快捷方法）。
    /// </summary>
    /// <param name="channel">逻辑通道标识。</param>
    /// <param name="data">消息载荷。</param>
    /// <param name="reliable">是否可靠传输。</param>
    public void BroadcastMessage(string channel, byte[] data, bool reliable = true)
    {
        _messageChannel.Broadcast(channel, data, reliable);
    }

    // ── 公共 API：主动退出 ──

    /// <summary>
    /// 主动退出：先发通知再延迟断开，让对端区分主动退出与意外断线。
    /// </summary>
    /// <param name="reason">退出原因，如 "quit"、"room"、"game"。</param>
    public async void GracefulDisconnect(string reason = "quit")
    {
        try
        {
            SendGracefulQuit(reason);
            var tree = GetTree();
            if (tree != null)
            {
                await ToSignal(tree.CreateTimer(0.2), "timeout");
            }
            Disconnect();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[EasyMultiplayer] GracefulDisconnect 异常: {ex.Message}");
            Disconnect();
        }
    }

    // ── 版本校验 ──

    /// <summary>
    /// Client 连接后自动发送版本号给 Host。
    /// </summary>
    private void SendVersionToHost()
    {
        _messageChannel.SendReliable(1, VersionChannel,
            Encoding.UTF8.GetBytes($"{MsgVersion}{GameVersion}"));
        _waitingVersionCheck = true;
        _versionCheckTimer = 0;
    }

    /// <summary>
    /// Host 处理收到的版本号。
    /// </summary>
    private void HandleVersionCheck(int peerId, string remoteVersion)
    {
        if (remoteVersion == GameVersion)
        {
            GD.Print($"[EasyMultiplayer] 版本校验通过: {remoteVersion}");
            // 回复确认
            _messageChannel.SendReliable(peerId, VersionChannel,
                Encoding.UTF8.GetBytes($"{MsgVersionOk}{GameVersion}"));
            EmitSignal(SignalName.VersionVerified, remoteVersion);
        }
        else
        {
            GD.PrintErr($"[EasyMultiplayer] 版本不匹配！本地={GameVersion}, 对端={remoteVersion}");
            // 通知 Client 版本不匹配
            _messageChannel.SendReliable(peerId, VersionChannel,
                Encoding.UTF8.GetBytes($"{MsgVersionMismatch}{GameVersion}"));
            EmitSignal(SignalName.VersionMismatch, GameVersion, remoteVersion);

            // 延迟 300ms 后踢出，让消息先送达
            var capturedId = peerId;
            var timer = GetTree().CreateTimer(0.3);
            timer.Timeout += () =>
            {
                _transport.DisconnectPeer(capturedId);
            };
        }
    }

    // ── 主动退出通知 ──

    /// <summary>
    /// 发送主动退出通知给所有对端。
    /// </summary>
    private void SendGracefulQuit(string reason)
    {
        GD.Print($"[EasyMultiplayer] 发送主动退出通知: reason={reason}");
        _messageChannel.Broadcast(QuitChannel,
            Encoding.UTF8.GetBytes($"{MsgQuit}{reason}"));
    }

    // ── 传输层事件处理 ──

    /// <summary>
    /// 对端连接事件。
    /// </summary>
    private void OnTransportPeerConnected(int peerId)
    {
        _connectedPeers.Add(peerId);
        GD.Print($"[EasyMultiplayer] OnTransportPeerConnected: peerId={peerId}, _connectedPeers.Count={_connectedPeers.Count}");

        // 如果在重连状态，由心跳管理器处理
        if (_state == ConnectionState.Reconnecting) return;

        if (_state == ConnectionState.Hosting)
        {
            State = ConnectionState.Connected;
            _heartbeat.TrackPeer(peerId);
            _heartbeat.Start();
        }

        EmitSignal(SignalName.PeerJoined, peerId);
    }

    /// <summary>
    /// 对端断开事件。
    /// </summary>
    private void OnTransportPeerDisconnected(int peerId)
    {
        _connectedPeers.Remove(peerId);

        // 如果对端是主动退出，不进入重连
        if (_gracefullyQuitPeers.Contains(peerId))
        {
            _gracefullyQuitPeers.Remove(peerId);
            GD.Print($"[EasyMultiplayer] 对端 {peerId} 主动退出，跳过重连");
            EmitSignal(SignalName.PeerLeft, peerId);

            if (_transport.IsServer && _connectedPeers.Count == 0)
            {
                State = ConnectionState.Hosting;
            }
            else if (!_transport.IsServer)
            {
                Disconnect();
            }
            return;
        }

        EmitSignal(SignalName.PeerLeft, peerId);

        // 如果在 Connected 状态且不是主动退出，由心跳管理器处理重连
        // （心跳管理器的 OnTransportPeerDisconnected 会触发重连逻辑）
        if (_state == ConnectionState.Connected)
        {
            State = ConnectionState.Reconnecting;
        }
        else if (_state == ConnectionState.Hosting && _connectedPeers.Count == 0)
        {
            // 保持 Hosting 状态
        }
        else if (!_transport.IsServer && _state != ConnectionState.Reconnecting)
        {
            Disconnect();
        }
    }

    /// <summary>
    /// Client 连接成功事件。
    /// </summary>
    private void OnTransportConnectionSucceeded()
    {
        GD.Print($"[EasyMultiplayer] OnTransportConnectionSucceeded: State={_state}");
        if (_state == ConnectionState.Reconnecting) return; // 由心跳管理器处理

        if (_state == ConnectionState.Joining)
        {
            _joiningTimer = 0;
            State = ConnectionState.Connected;
            _connectedPeers.Add(1); // Server peer ID = 1
            _heartbeat.TrackPeer(1);
            _heartbeat.Start();

            EmitSignal(SignalName.ConnectionSucceeded);

            // 自动发送版本校验
            CallDeferred(MethodName.SendVersionToHost);
        }
    }

    /// <summary>
    /// Client 连接失败事件。
    /// </summary>
    private void OnTransportConnectionFailed()
    {
        if (_state == ConnectionState.Reconnecting) return; // 由心跳管理器处理

        State = ConnectionState.Disconnected;
        EmitSignal(SignalName.ConnectionFailed);
    }

    // ── 心跳事件处理 ──

    /// <summary>
    /// 对端心跳超时。
    /// </summary>
    private void OnHeartbeatPeerTimedOut(int peerId)
    {
        if (_state == ConnectionState.Connected)
        {
            State = ConnectionState.Reconnecting;
        }
    }

    /// <summary>
    /// 对端重连成功。
    /// </summary>
    private void OnHeartbeatPeerReconnected(int peerId)
    {
        _connectedPeers.Add(peerId);
        State = ConnectionState.Connected;
    }

    /// <summary>
    /// Host 端重连等待超时。
    /// </summary>
    private void OnHeartbeatReconnectTimedOut()
    {
        Disconnect();
    }

    /// <summary>
    /// Client 端重连全部失败。
    /// </summary>
    private void OnHeartbeatReconnectFailed()
    {
        Disconnect();
    }

    /// <summary>
    /// 重连后全量同步请求。
    /// </summary>
    private void OnHeartbeatFullSyncRequested(int peerId)
    {
        EmitSignal(SignalName.FullSyncRequested, peerId);
    }

    // ── 内部消息处理 ──

    /// <summary>
    /// 处理版本校验和主动退出等内部消息。
    /// </summary>
    private void OnInternalMessageReceived(int peerId, string channel, byte[] data)
    {
        if (channel == VersionChannel)
        {
            HandleVersionMessage(peerId, data);
        }
        else if (channel == QuitChannel)
        {
            HandleQuitMessage(peerId, data);
        }
    }

    /// <summary>
    /// 处理版本校验消息。
    /// </summary>
    private void HandleVersionMessage(int peerId, byte[] data)
    {
        var msg = Encoding.UTF8.GetString(data);

        if (msg.StartsWith(MsgVersion) && _transport.IsServer)
        {
            // Host 收到 Client 的版本号
            var remoteVersion = msg.Substring(MsgVersion.Length);
            HandleVersionCheck(peerId, remoteVersion);
        }
        else if (msg.StartsWith(MsgVersionOk) && !_transport.IsServer)
        {
            // Client 收到版本校验通过
            _waitingVersionCheck = false;
            var hostVersion = msg.Substring(MsgVersionOk.Length);
            GD.Print($"[EasyMultiplayer] Host 版本确认: {hostVersion}");
            EmitSignal(SignalName.VersionVerified, hostVersion);
        }
        else if (msg.StartsWith(MsgVersionMismatch) && !_transport.IsServer)
        {
            // Client 收到版本不匹配
            _waitingVersionCheck = false;
            var hostVersion = msg.Substring(MsgVersionMismatch.Length);
            GD.PrintErr($"[EasyMultiplayer] 版本不匹配！Host={hostVersion}, 本地={GameVersion}");
            EmitSignal(SignalName.VersionMismatch, GameVersion, hostVersion);
            // Host 会延迟踢出，Client 不需要主动断开
        }
    }

    /// <summary>
    /// 处理主动退出消息。
    /// </summary>
    private void HandleQuitMessage(int peerId, byte[] data)
    {
        var msg = Encoding.UTF8.GetString(data);

        if (msg.StartsWith(MsgQuit))
        {
            var reason = msg.Substring(MsgQuit.Length);
            GD.Print($"[EasyMultiplayer] 收到对端 {peerId} 主动退出通知: reason={reason}");
            _gracefullyQuitPeers.Add(peerId);
            EmitSignal(SignalName.PeerGracefulQuit, peerId, reason);
        }
    }

    // ── 房间模块状态同步 ──

    /// <summary>
    /// 同步 RoomHost 状态到 EasyMultiplayer 连接状态。
    /// </summary>
    private void OnRoomHostStateChanged(int oldState, int newState)
    {
        // RoomState.Waiting = 1：房间已创建并等待玩家，同步为 Hosting
        if (newState == (int)RoomState.Waiting && _state == ConnectionState.Disconnected)
        {
            State = ConnectionState.Hosting;
        }
        // RoomState.Closed = 4：房间已关闭，同步为 Disconnected
        else if (newState == (int)RoomState.Closed && _state != ConnectionState.Disconnected)
        {
            State = ConnectionState.Disconnected;
        }
    }

    /// <summary>
    /// 同步 RoomClient 状态到 EasyMultiplayer 连接状态。
    /// </summary>
    private void OnRoomClientStateChanged(int oldState, int newState)
    {
        // ClientState.Joining = 2：正在加入房间，同步为 Joining
        if (newState == (int)ClientState.Joining && _state == ConnectionState.Disconnected)
        {
            State = ConnectionState.Joining;
            _joiningTimer = 0;
        }
        // ClientState.Idle = 0：回到空闲（加入失败），仅当 RoomClient 是从 Joining 状态退回时才重置。
        // 注意：Searching → Idle（StopSearching）不应影响 EasyMP 的 Joining 状态。
        else if (newState == (int)ClientState.Idle && oldState == (int)ClientState.Joining
                 && _state == ConnectionState.Joining)
        {
            GD.Print($"[EasyMultiplayer] OnRoomClientStateChanged: RoomClient Joining→Idle，重置为 Disconnected");
            State = ConnectionState.Disconnected;
        }
        else
        {
            GD.Print($"[EasyMultiplayer] OnRoomClientStateChanged: {(ClientState)oldState} → {(ClientState)newState}，EasyMP.State={_state}（无操作）");
        }
    }
}
