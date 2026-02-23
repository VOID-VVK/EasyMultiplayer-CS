using Godot;
using System;

namespace EasyMultiplayer.Transport;

/// <summary>
/// 基于 Godot ENetMultiplayerPeer 的默认传输层实现。
/// </summary>
/// <remarks>
/// <para>
/// 封装 Godot 的 <see cref="ENetMultiplayerPeer"/>，将 Godot Multiplayer 回调
/// 映射到 <see cref="ITransport"/> 事件，使上层代码与 Godot 具体实现解耦。
/// </para>
/// <para>
/// 使用者需在每帧调用 <see cref="Poll"/> 以驱动事件循环（通常由 EasyMultiplayer 单例的 _Process 调用）。
/// </para>
/// </remarks>
public class ENetTransport : ITransport
{
    private ENetMultiplayerPeer? _peer;
    private SceneTree? _sceneTree;

    /// <summary>记住上次连接的地址，用于重连。</summary>
    private string _lastAddress = "";

    /// <summary>记住上次连接的端口，用于重连。</summary>
    private int _lastPort;

    // ── ITransport 事件 ──

    /// <inheritdoc />
    public event Action<int>? PeerConnected;

    /// <inheritdoc />
    public event Action<int>? PeerDisconnected;

    /// <inheritdoc />
    public event Action<int, int, byte[]>? DataReceived;

    /// <inheritdoc />
    public event Action? ConnectionSucceeded;

    /// <inheritdoc />
    public event Action? ConnectionFailed;

    // ── ITransport 属性 ──

    /// <inheritdoc />
    public bool IsServer { get; private set; }

    /// <inheritdoc />
    public int UniqueId { get; private set; }

    /// <inheritdoc />
    public TransportStatus Status { get; private set; } = TransportStatus.Disconnected;

    /// <summary>
    /// 初始化传输层，绑定 Godot SceneTree 的 Multiplayer 回调。
    /// </summary>
    /// <param name="sceneTree">当前场景树，用于访问 <see cref="SceneTree.Root"/> 的 Multiplayer。</param>
    public void Initialize(SceneTree sceneTree)
    {
        _sceneTree = sceneTree;
        var mp = GetMultiplayer();
        if (mp == null) return;

        mp.PeerConnected += OnPeerConnected;
        mp.PeerDisconnected += OnPeerDisconnected;
        mp.ConnectedToServer += OnConnectedToServer;
        mp.ConnectionFailed += OnConnectionFailed;
        mp.ServerDisconnected += OnServerDisconnected;
    }

    /// <summary>
    /// 清理回调绑定。应在不再使用此传输层时调用。
    /// </summary>
    public void Cleanup()
    {
        var mp = GetMultiplayer();
        if (mp == null) return;

        mp.PeerConnected -= OnPeerConnected;
        mp.PeerDisconnected -= OnPeerDisconnected;
        mp.ConnectedToServer -= OnConnectedToServer;
        mp.ConnectionFailed -= OnConnectionFailed;
        mp.ServerDisconnected -= OnServerDisconnected;
    }

    // ── ITransport 生命周期 ──

    /// <inheritdoc />
    public Error CreateHost(int port, int maxClients)
    {
        if (Status != TransportStatus.Disconnected)
        {
            GD.PrintErr("[ENetTransport] 无法创建主机：当前状态不是 Disconnected");
            return Error.AlreadyInUse;
        }

        _peer = new ENetMultiplayerPeer();
        var error = _peer.CreateServer(port, maxClients);
        if (error != Error.Ok)
        {
            GD.PrintErr($"[ENetTransport] 创建主机失败: {error}");
            _peer = null;
            return error;
        }

        var mp = GetMultiplayer();
        if (mp != null)
        {
            mp.MultiplayerPeer = _peer;
        }

        _lastPort = port;
        IsServer = true;
        UniqueId = 1;
        Status = TransportStatus.Connected;
        GD.Print($"[ENetTransport] 主机已创建，端口: {port}, 最大客户端: {maxClients}");
        return Error.Ok;
    }

    /// <inheritdoc />
    public Error CreateClient(string address, int port)
    {
        if (Status != TransportStatus.Disconnected)
        {
            GD.PrintErr("[ENetTransport] 无法连接：当前状态不是 Disconnected");
            return Error.AlreadyInUse;
        }

        _peer = new ENetMultiplayerPeer();
        var error = _peer.CreateClient(address, port);
        if (error != Error.Ok)
        {
            GD.PrintErr($"[ENetTransport] 连接失败: {error}");
            _peer = null;
            return error;
        }

        var mp = GetMultiplayer();
        if (mp != null)
        {
            mp.MultiplayerPeer = _peer;
        }

        _lastAddress = address;
        _lastPort = port;
        IsServer = false;
        Status = TransportStatus.Connecting;
        GD.Print($"[ENetTransport] 正在连接 {address}:{port}");
        return Error.Ok;
    }

    /// <inheritdoc />
    public void Disconnect()
    {
        if (_peer == null) return;

        _peer.Close();
        var mp = GetMultiplayer();
        if (mp != null)
        {
            mp.MultiplayerPeer = null;
        }

        _peer = null;
        IsServer = false;
        UniqueId = 0;
        Status = TransportStatus.Disconnected;
        GD.Print("[ENetTransport] 已断开连接");
    }

    /// <inheritdoc />
    public void DisconnectPeer(int peerId)
    {
        if (_peer == null) return;
        _peer.DisconnectPeer(peerId);
        GD.Print($"[ENetTransport] 已断开对端: {peerId}");
    }

    /// <inheritdoc />
    public void Poll()
    {
        // 从 ENetMultiplayerPeer 读取所有可用数据包并触发 DataReceived 事件
        if (_peer == null || Status == TransportStatus.Disconnected) return;

        while (_peer.GetAvailablePacketCount() > 0)
        {
            var rawPacket = _peer.GetPacket();
            var senderId = _peer.GetPacketPeer();

            if (rawPacket != null && rawPacket.Length >= 4)
            {
                var (channel, data) = ParsePacket(rawPacket);
                DataReceived?.Invoke((int)senderId, channel, data);
            }
        }
    }

    // ── ITransport 数据发送 ──

    /// <inheritdoc />
    public void SendReliable(int peerId, int channel, byte[] data)
    {
        if (_peer == null || Status == TransportStatus.Disconnected) return;

        _peer.TransferMode = MultiplayerPeer.TransferModeEnum.Reliable;

        var packet = BuildPacket(channel, data);
        SendPacket(peerId, packet);
    }

    /// <inheritdoc />
    public void SendUnreliable(int peerId, int channel, byte[] data)
    {
        if (_peer == null || Status == TransportStatus.Disconnected) return;

        _peer.TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable;

        var packet = BuildPacket(channel, data);
        SendPacket(peerId, packet);
    }

    /// <summary>
    /// 通过 ENetMultiplayerPeer 发送数据包。
    /// 使用 <see cref="ENetMultiplayerPeer.GetPeer"/> 获取 ENetPacketPeer 直接发送。
    /// </summary>
    private void SendPacket(int peerId, byte[] packet)
    {
        if (_peer == null) return;

        if (peerId == 0)
        {
            // 广播：设置 target peer 为 0，通过 PutPacket 发送
            _peer.SetTargetPeer(0);
            _peer.PutPacket(packet);
        }
        else
        {
            _peer.SetTargetPeer(peerId);
            _peer.PutPacket(packet);
        }
    }

    // ── 辅助方法 ──

    /// <summary>
    /// 获取当前 SceneTree 的 Multiplayer API。
    /// </summary>
    private MultiplayerApi? GetMultiplayer()
    {
        return _sceneTree?.Root?.Multiplayer;
    }

    /// <summary>
    /// 构建数据包：在数据前附加通道编号（4 字节 int），便于接收端解析。
    /// </summary>
    /// <param name="channel">逻辑通道编号。</param>
    /// <param name="data">原始数据载荷。</param>
    /// <returns>带通道头的完整数据包。</returns>
    private static byte[] BuildPacket(int channel, byte[] data)
    {
        var packet = new byte[4 + data.Length];
        BitConverter.GetBytes(channel).CopyTo(packet, 0);
        data.CopyTo(packet, 4);
        return packet;
    }

    /// <summary>
    /// 解析数据包，提取通道编号和原始数据。
    /// </summary>
    /// <param name="packet">带通道头的完整数据包。</param>
    /// <returns>通道编号和原始数据载荷。</returns>
    private static (int channel, byte[] data) ParsePacket(byte[] packet)
    {
        if (packet.Length < 4)
            return (0, packet);

        var channel = BitConverter.ToInt32(packet, 0);
        var data = new byte[packet.Length - 4];
        Array.Copy(packet, 4, data, 0, data.Length);
        return (channel, data);
    }

    /// <summary>
    /// 获取上次连接的地址（用于重连）。
    /// </summary>
    public string LastAddress => _lastAddress;

    /// <summary>
    /// 获取上次连接的端口（用于重连）。
    /// </summary>
    public int LastPort => _lastPort;

    // ── Godot Multiplayer 回调 ──

    private void OnPeerConnected(long peerId)
    {
        GD.Print($"[ENetTransport] 对端已连接: {peerId}");
        PeerConnected?.Invoke((int)peerId);
    }

    private void OnPeerDisconnected(long peerId)
    {
        GD.Print($"[ENetTransport] 对端已断开: {peerId}");
        PeerDisconnected?.Invoke((int)peerId);
    }

    private void OnConnectedToServer()
    {
        var mp = GetMultiplayer();
        if (mp != null)
        {
            UniqueId = mp.GetUniqueId();
        }
        Status = TransportStatus.Connected;
        GD.Print($"[ENetTransport] 已连接到服务器, UniqueId={UniqueId}");
        ConnectionSucceeded?.Invoke();
    }

    private void OnConnectionFailed()
    {
        GD.PrintErr("[ENetTransport] 连接服务器失败");
        Status = TransportStatus.Disconnected;
        _peer = null;
        var mp = GetMultiplayer();
        if (mp != null)
        {
            mp.MultiplayerPeer = null;
        }
        ConnectionFailed?.Invoke();
    }

    private void OnServerDisconnected()
    {
        GD.Print("[ENetTransport] 服务器已断开");
        _peer?.Close();
        _peer = null;
        var mp = GetMultiplayer();
        if (mp != null) mp.MultiplayerPeer = null;
        IsServer = false;
        UniqueId = 0;
        Status = TransportStatus.Disconnected;
        PeerDisconnected?.Invoke(1); // Server peer ID is always 1
    }
}
