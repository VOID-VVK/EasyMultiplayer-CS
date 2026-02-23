# EasyMultiplayer 测试报告

> 测试日期：2026-02-23  
> 测试范围：代码审查 + 需求匹配 + 编译检查  
> 测试基准：`docs/design.md`

---

## 1. 编译结果

```
dotnet build → 已成功生成。0 个警告，0 个错误。
```

✅ 编译通过

---

## 2. 代码审查结果

### 2.1 审查通过项

| 文件 | 代码逻辑 | 命名规范 | XML 文档 | 异常处理 |
|------|---------|---------|---------|---------|
| plugin.cfg | ✅ | ✅ | N/A | N/A |
| EasyMultiplayerPlugin.cs | ✅ | ✅ | ✅ | ✅ |
| ConnectionState.cs | ✅ | ✅ | ✅ | N/A |
| EasyMultiplayerConfig.cs | ✅ | ✅ | ✅ | N/A |
| MessageChannel.cs | ✅ | ✅ | ✅ | ✅ |
| ITransport.cs | ✅ | ✅ | ✅ | N/A |
| IDiscovery.cs | ✅ | ✅ | ✅ | N/A |
| RoomState.cs | ✅ | ✅ | ✅ | N/A |
| HeartbeatManager.cs | ✅ | ✅ | ✅ | ✅ |

### 2.2 发现的问题

---

#### 🔴 Critical

**C1 — ENetTransport.OnServerDisconnected 未清理 _peer 和 MultiplayerPeer**

- 文件：`Transport/ENetTransport.cs`，`OnServerDisconnected()` 方法
- 描述：当服务器断开时，`OnServerDisconnected` 仅设置 `Status = Disconnected` 并触发 `PeerDisconnected(1)`，但没有执行 `_peer.Close()` / `_peer = null` / `mp.MultiplayerPeer = null`。这导致 ENetMultiplayerPeer 资源泄漏，且后续 `CreateClient` 会因 `Status != Disconnected` 检查通过但 `_peer` 仍存在而产生不可预期行为。
- 对比：`OnConnectionFailed()` 正确清理了 `_peer = null` 和 `mp.MultiplayerPeer = null`，但 `OnServerDisconnected` 遗漏了。
- 影响：客户端断线后无法正确重连；资源泄漏。
- 建议修复：
```csharp
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
    PeerDisconnected?.Invoke(1);
}
```

**C2 — HeartbeatManager 自动重连与 ENetTransport 状态冲突**

- 文件：`Heartbeat/HeartbeatManager.cs`，`ProcessClientAutoReconnect()` 方法
- 描述：自动重连时先调用 `_transport.Disconnect()`，然后调用 `_transport.CreateClient()`。但 `ENetTransport.CreateClient()` 有前置检查 `if (Status != TransportStatus.Disconnected) return Error.AlreadyInUse`。如果 `Disconnect()` 后 `Status` 没有正确重置为 `Disconnected`（例如因为 C1 的问题），重连将永远失败。
- 此外，`Disconnect()` 调用后 `_transport` 的事件回调仍然绑定在 HeartbeatManager 上，`Disconnect()` 会触发 `OnServerDisconnected` → `PeerDisconnected` → `OnTransportPeerDisconnected`，可能导致重复进入重连逻辑。
- 影响：客户端自动重连可能完全不工作或产生状态混乱。
- 建议：在 `ProcessClientAutoReconnect` 中增加状态检查，确保 `Disconnect()` 后 `Status` 确实为 `Disconnected`；或在重连期间临时解绑事件回调。

**C3 — RoomHost.SetHostReady 状态守卫过严**

- 文件：`Room/RoomHost.cs`，`SetHostReady()` 方法
- 描述：`SetHostReady` 要求 `State == RoomState.Ready`，但 `Ready` 状态仅在有客人加入后才会进入。这意味着 Host 无法在 `Waiting` 状态预设准备状态。虽然这可能是设计意图，但与设计文档中 `SetHostReady(bool ready)` 的 API 描述不一致——文档未提及此限制。
- 更严重的是：如果 MaxPlayers > 2，有 1 个客人加入后进入 Ready，Host 设置 ready，然后第 2 个客人加入，此时 Host 的 ready 状态不会被重置，但新客人的 ready 是 false，`CheckAllReady` 不会触发。这是正确的。但如果客人离开又重新加入，`OnPeerDisconnected` 不会重置 HostReady，可能导致状态不一致。
- 影响：多人场景下准备状态管理可能出现边界问题。

---

#### 🟡 Major

**M1 — RoomHost.CreateRoom 中 Version 字段设置错误**

- 文件：`Room/RoomHost.cs`，`CreateRoom()` 方法
- 描述：广播的 `RoomInfo.Version` 被设置为 `_config.Port.ToString()`（端口号的字符串），而非游戏版本号。
```csharp
Version = _config.Port.ToString() // 将在 EasyMultiplayer 中设置正确的版本
```
- 注释说"将在 EasyMultiplayer 中设置正确的版本"，但实际上 `EasyMultiplayer.cs` 中的 `CreateRoom` 快捷方法直接透传调用，并未修正 Version 字段。
- 影响：客户端发现的房间版本号显示为端口号而非实际游戏版本，版本校验信息不准确。
- 建议：`RoomHost.Setup()` 接收 `GameVersion` 参数，或在 `EasyMultiplayer.CreateRoom()` 中设置正确的版本。

**M2 — MessageChannel 与 HeartbeatManager 共享 TransportChannel 0 和 255 的潜在冲突**

- 文件：`Core/MessageChannel.cs` + `Heartbeat/HeartbeatManager.cs`
- 描述：`MessageChannel` 使用 `TransportChannel = 0`，`HeartbeatManager` 使用 `HeartbeatChannel = 255`。两者都监听 `ITransport.DataReceived` 事件。`ENetTransport.Poll()` 中通过 `BuildPacket/ParsePacket` 在数据前附加 4 字节 channel 头。`MessageChannel.OnDataReceived` 通过 `if (transportChannel != TransportChannel) return` 过滤，`HeartbeatManager.OnDataReceived` 通过 `if (channel != HeartbeatChannel) return` 过滤。
- 但 `ENetTransport.SendReliable/SendUnreliable` 同时设置了 `_peer.TransferChannel = channel`（ENet 层通道）和 `BuildPacket(channel, data)`（自定义头）。ENet 的 `TransferChannel` 和自定义头中的 channel 是冗余的。更重要的是，`_peer.TransferChannel` 的设置不是线程安全的——如果在同一帧内 MessageChannel 和 HeartbeatManager 都发送数据，`TransferChannel` 可能被覆盖。
- 影响：在极端情况下（同一帧内多次发送），ENet 层的通道可能与自定义头不一致，但由于接收端使用自定义头解析，实际影响有限。不过这是一个设计冗余。
- 建议：移除 `_peer.TransferChannel` 的设置，统一使用自定义头中的 channel 编号。

**M3 — UdpBroadcastDiscovery 监听端可能收到自己的广播**

- 文件：`Discovery/UdpBroadcastDiscovery.cs`
- 描述：如果同一台机器同时运行 Host（广播）和 Client（监听），监听端会收到自己发出的广播包。当前没有过滤自身广播的逻辑。
- 影响：在开发/测试环境中，同一台机器上的 Client 会发现自己创建的房间，可能导致混淆。
- 建议：在 `HandleRoomFound` 中过滤本机 IP，或在 `RoomInfo` 中添加唯一标识用于过滤。

**M4 — EasyMultiplayer.cs 中 `new bool IsConnected` 隐藏基类成员**

- 文件：`Core/EasyMultiplayer.cs`
- 描述：`public new bool IsConnected` 使用 `new` 关键字隐藏了 `GodotObject.IsConnected` 方法。虽然编译器不报错（因为签名不同，一个是属性一个是方法），但这可能在 GDScript 互操作时造成混淆。
- 影响：从 GDScript 调用 `is_connected` 时行为可能不符合预期。
- 建议：重命名为 `IsNetworkConnected` 或类似名称。

**M5 — GracefulDisconnect 使用 async void 无异常保护**

- 文件：`Core/EasyMultiplayer.cs`，`GracefulDisconnect()` 方法
- 描述：`async void` 方法中如果 `GetTree()` 返回 null（例如节点已从场景树移除），`CreateTimer` 会抛出 NullReferenceException，且由于 `async void` 无法被调用者捕获。
- 影响：在节点生命周期边界调用可能导致未处理异常。
- 建议：添加 null 检查或 try-catch。

**M6 — RoomHost.OnPeerDisconnected 中重新广播时未设置 Version**

- 文件：`Room/RoomHost.cs`，`OnPeerDisconnected()` 方法
- 描述：客人离开后重新广播时创建的 `RoomInfo` 没有设置 `Version` 字段，使用默认值 `"1.0.0"`，与 `CreateRoom` 中的行为不一致。
- 影响：广播信息中的版本号不一致。

**M7 — 设计文档中 GuestReadyChanged 信号参数为 `(bool ready)`，实现为 `(int peerId, bool ready)`**

- 文件：`Room/RoomHost.cs`
- 描述：设计文档 §6.1 定义 `GuestReadyChangedEventHandler(bool ready)`，但实现中为 `GuestReadyChangedEventHandler(int peerId, bool ready)`。实现版本更合理（多人场景需要知道是哪个客人），但与设计文档不一致。
- 影响：使用者按设计文档编码会编译失败。
- 建议：更新设计文档以匹配实现。

---

#### 🟢 Minor

**m1 — 设计文档中 `TransportStatus.cs` 和 `RoomInfo.cs` 作为独立文件列出，实际合并到其他文件中**

- 设计文档 §13 目录结构列出 `Transport/TransportStatus.cs` 和 `Discovery/RoomInfo.cs` 为独立文件，但实际实现中：
  - `TransportStatus` 枚举合并在 `ITransport.cs` 中
  - `RoomInfo` 和 `DiscoveredRoom` 类合并在 `IDiscovery.cs` 中
- 影响：无功能影响，但与设计文档的目录结构描述不一致。

**m2 — MessageChannel.PackMessage 使用 BitConverter 存在字节序问题**

- 文件：`Core/MessageChannel.cs`
- 描述：`BitConverter.GetBytes(channelLen)` 在不同平台上可能产生不同字节序（大端/小端）。虽然局域网场景下通常是同构平台，但跨平台时可能出问题。
- 影响：极低风险，仅在异构平台局域网中可能出现。
- 建议：可考虑使用 `BinaryPrimitives.WriteUInt16LittleEndian` 明确字节序。

**m3 — ENetTransport.Poll() 中 rawPacket.Length >= 4 的硬编码检查**

- 文件：`Transport/ENetTransport.cs`
- 描述：`Poll()` 中检查 `rawPacket.Length >= 4` 才处理数据包，但没有日志说明为什么丢弃小包。如果某个实现发送了小于 4 字节的包（不含自定义头），会被静默丢弃。
- 影响：调试困难。
- 建议：添加警告日志。

**m4 — HeartbeatManager 中 _lastPongReceived 使用系统时间而非引擎时间**

- 文件：`Heartbeat/HeartbeatManager.cs`
- 描述：`_lastPongReceived` 使用 `Time.GetUnixTimeFromSystem()`（系统时间），而超时检查也使用系统时间，所以逻辑上是一致的。但如果系统时间被修改（如 NTP 同步跳变），可能导致误判。引擎时间 `Time.GetTicksMsec()` 更稳定。
- 影响：极低风险。

**m5 — UdpBroadcastDiscovery.CleanupStaleRooms 每帧执行**

- 文件：`Discovery/UdpBroadcastDiscovery.cs`
- 描述：`CleanupStaleRooms()` 在每帧 `_Process` 中调用，遍历所有已发现房间。房间数量通常很少，性能影响可忽略，但可以优化为每秒执行一次。
- 影响：微小的性能浪费。

**m6 — EasyMultiplayerPlugin.cs 未注册 Autoload**

- 文件：`EasyMultiplayerPlugin.cs`
- 描述：`EditorPlugin` 的 `_EnterTree` 仅打印日志，未调用 `AddAutoloadSingleton` 注册 EasyMultiplayer 单例。使用者需要手动在 Project Settings 中添加 Autoload。
- 影响：使用便利性降低，但不影响功能。
- 建议：在 `_EnterTree` 中自动注册 Autoload。

**m7 — RoomClient._ExitTree 中 StopSearching 的条件检查不完整**

- 文件：`Room/RoomClient.cs`
- 描述：`_ExitTree` 中 `if (State != ClientState.Idle) StopSearching()`，但 `StopSearching` 内部只在 `State == Searching` 时才改状态。如果 State 是 InRoom 或 GameStarting，`StopSearching` 只会调用 `_discovery?.StopListening()` 但不会清理房间连接。应该调用 `LeaveRoom()` 而非 `StopSearching()`。
- 影响：节点销毁时可能未正确清理连接。

---

## 3. 需求匹配度

| 需求项 | 状态 | 备注 |
|--------|------|------|
| ITransport 接口按设计实现 | ✅ | 完全匹配 |
| IDiscovery 接口按设计实现 | ✅ | 完全匹配 |
| 连接状态机完整 (Disconnected → Hosting/Joining → Connected → Reconnecting) | ✅ | 所有状态和转换均已实现 |
| 房间系统状态机完整 (RoomHost: Idle→Waiting→Ready→Playing→Closed) | ✅ | 完整实现，增加了 Closed 状态 |
| 房间系统状态机完整 (RoomClient: Idle→Searching→Joining→InRoom→GameStarting) | ✅ | 完全匹配 |
| 心跳/重连机制 | ✅ | Ping/Pong、RTT、NetQuality、Host 等待、Client 自动重连均已实现 |
| 版本校验 | ✅ | Client 发送→Host 比对→通过/踢出，延迟 300ms 踢出 |
| 通用消息通道 | ✅ | SendReliable/SendUnreliable/Broadcast + 频率限制 |
| 超时兜底 (Joining) | ⚠️ | 依赖 ENet 自身超时，未在插件层显式设置 10s 超时 |
| 超时兜底 (Reconnecting) | ✅ | Host 30s + Client MaxAttempts × RetryInterval |
| 超时兜底 (版本校验等待) | ⚠️ | 未实现 5s 版本校验超时，如果 Host 不回复，Client 会一直等待 |
| 取消机制 (Searching) | ✅ | StopSearching() |
| 主动退出通知 | ✅ | GracefulDisconnect + PeerGracefulQuit 信号 |
| 状态重置 | ✅ | Disconnect() 重置所有状态 |
| _ExitTree 清理 | ✅ | 所有模块均实现 |
| 区分退出类型 | ✅ | _gracefullyQuitPeers 集合区分主动/意外 |
| MaxClients 可配置 | ✅ | Config.MaxClients，RoomHost 支持多人 |
| 信号一览表 — 连接模块 | ✅ | StateChanged, PeerJoined, PeerLeft, ConnectionSucceeded, ConnectionFailed, VersionVerified, VersionMismatch, PeerGracefulQuit 全部实现 |
| 信号一览表 — RoomHost | ⚠️ | GuestReadyChanged 参数与设计不一致（多了 peerId），其余全部匹配 |
| 信号一览表 — RoomClient | ✅ | 全部匹配 |
| 信号一览表 — HeartbeatManager | ✅ | NetQualityChanged, PeerTimedOut, PeerReconnected, ReconnectTimedOut, ReconnectFailed 全部实现 |
| 信号一览表 — MessageChannel | ✅ | MessageReceived 已实现 |
| 信号一览表 — FullSyncRequested | ✅ | 在 HeartbeatManager 和 EasyMultiplayer 中均已实现 |

### 需求匹配度：约 90%

未完全匹配的项：
1. Joining 超时未在插件层显式实现（依赖 ENet 底层超时）
2. 版本校验等待 5s 超时未实现
3. GuestReadyChanged 信号参数与设计文档不一致（实现更优，建议更新文档）
4. 目录结构与设计文档略有差异（文件合并）

---

## 4. 改进建议

### 高优先级
1. **修复 C1**：`ENetTransport.OnServerDisconnected` 必须清理 `_peer` 和 `MultiplayerPeer`，否则客户端断线后无法重连
2. **修复 C2**：审查 HeartbeatManager 自动重连流程，确保与 ENetTransport 状态一致，避免重连死循环
3. **修复 M1**：RoomHost 广播的 Version 字段应使用实际游戏版本号

### 中优先级
4. 添加 Joining 超时（10s）和版本校验超时（5s）的显式实现
5. 修复 `GracefulDisconnect` 的 async void 异常保护
6. 重命名 `IsConnected` 避免与 GodotObject 冲突
7. 修复 RoomClient._ExitTree 中的清理逻辑
8. 更新设计文档中 GuestReadyChanged 的参数签名

### 低优先级
9. EasyMultiplayerPlugin 中自动注册 Autoload
10. MessageChannel 使用明确字节序
11. 优化 CleanupStaleRooms 执行频率
12. ENetTransport 中移除冗余的 TransferChannel 设置

---

## 5. 总结

| 指标 | 结果 |
|------|------|
| 编译 | ✅ 通过（0 警告 0 错误） |
| Critical 问题 | 3 个 |
| Major 问题 | 7 个 |
| Minor 问题 | 7 个 |
| 需求匹配度 | ~90% |
| 代码质量 | 良好（命名规范一致、XML 文档完整、架构清晰） |
| 整体评价 | 架构设计优秀，代码质量较高。主要问题集中在 ENetTransport 断线清理和重连流程的状态一致性上，修复后即可投入使用。 |

---

## 回归验证（修复后）

日期：2026-02-23
验证结果：✅ 全部通过

### 🔴 Critical 修复验证

**C1 — ENetTransport.OnServerDisconnected 清理** ✅ 已修复
- `OnServerDisconnected()` 现在正确执行：`_peer?.Close()` → `_peer = null` → `mp.MultiplayerPeer = null` → `IsServer = false` → `UniqueId = 0` → `Status = Disconnected`
- 与 `OnConnectionFailed()` 和 `Disconnect()` 的清理逻辑一致，资源泄漏和重连失败问题已解决

**C2 — HeartbeatManager.ProcessClientAutoReconnect 重连安全性** ✅ 已修复
- 重连前临时解绑 `PeerDisconnected` 和 `ConnectionFailed` 回调，防止 `Disconnect()` 触发的事件导致重复进入重连逻辑
- `Disconnect()` 后显式检查 `Status == Disconnected`，不满足则中止重连并报错
- 重连尝试后重新绑定回调，流程完整

**C3 — RoomHost.OnPeerDisconnected 重置 HostReady** ✅ 已修复
- `OnPeerDisconnected()` 中添加了 `HostReady = false`，确保客人离开后准备状态被重置，避免多人场景下的状态不一致

### 🟡 Major 修复验证

**M1 — RoomHost 广播 Version 使用正确版本号** ✅ 已修复
- `RoomHost.Setup()` 新增 `gameVersion` 参数，保存到 `_gameVersion` 字段
- `CreateRoom()` 中广播的 `RoomInfo.Version = _gameVersion`，不再使用 `_config.Port.ToString()`
- `EasyMultiplayer._Ready()` 中调用 `_roomHost.Setup(..., GameVersion)` 传入正确版本号

**M2 — ENetTransport 移除冗余 TransferChannel 设置** ✅ 已修复
- `SendReliable()` 和 `SendUnreliable()` 中不再设置 `_peer.TransferChannel`
- 仅设置 `_peer.TransferMode`（Reliable/Unreliable），通道编号统一通过 `BuildPacket` 自定义头传递
- 消除了同帧多次发送时 `TransferChannel` 被覆盖的竞态风险

**M3 — UdpBroadcastDiscovery 过滤本机 IP 广播** ✅ 已修复
- 新增 `_localIps` 集合和 `CollectLocalIps()` 方法，在 `StartListening()` 时收集本机所有 IP（含 127.0.0.1、::1 和所有网卡地址）
- `HandleRoomFound()` 开头检查 `if (_localIps.Contains(ip)) return`，过滤自身广播

**M4 — IsConnected 重命名为 IsNetworkConnected** ✅ 已修复
- `EasyMultiplayer.cs` 中属性已重命名为 `public bool IsNetworkConnected`，不再使用 `new` 关键字隐藏基类成员
- 消除了与 `GodotObject.IsConnected` 的命名冲突

**M5 — GracefulDisconnect 异常保护** ✅ 已修复
- 整个方法体包裹在 `try-catch` 中
- `GetTree()` 返回值赋给局部变量 `tree`，null 检查后才调用 `CreateTimer`
- catch 块中打印错误并调用 `Disconnect()` 确保资源释放

**M6 — 重新广播时 Version 正确** ✅ 已修复
- `OnPeerDisconnected()` 中所有重新广播的 `RoomInfo` 均使用 `Version = _gameVersion`
- `UpdateBroadcast()` 方法中同样使用 `Version = _gameVersion`
- 与 `CreateRoom()` 中的行为一致

**M7 — 设计文档 GuestReadyChanged 参数更新** ✅ 已修复
- `docs/design.md` §6.1 中 `GuestReadyChangedEventHandler(int peerId, bool ready)` — 与实现一致
- §14.2 信号一览表中也已更新为 `(int peerId, bool ready)`

### 额外修复验证

**Joining 超时（10s）** ✅ 已实现
- `EasyMultiplayer._Process()` 中当 `_state == ConnectionState.Joining` 时累加 `_joiningTimer`
- 超过 `JoiningTimeout = 10.0` 秒后调用 `Disconnect()` 并触发 `ConnectionFailed` 信号

**版本校验超时（5s）** ✅ 已实现
- `_waitingVersionCheck` 标志在 `SendVersionToHost()` 中设为 true
- `_Process()` 中当 `_waitingVersionCheck` 为 true 时累加 `_versionCheckTimer`
- 超过 `VersionCheckTimeout = 5.0` 秒后断开连接并触发 `ConnectionFailed`
- 收到版本校验结果（OK 或 Mismatch）时正确重置标志

**RoomClient._ExitTree 清理逻辑** ✅ 已修复
- `_ExitTree()` 中区分状态处理：`Searching` → `StopSearching()`；其他非 Idle 状态（InRoom、GameStarting、Joining）→ `LeaveRoom()`
- `LeaveRoom()` 正确调用 `_transport?.Disconnect()` 并重置所有状态

### 回归检查

**编译** ✅ 通过
```
dotnet build → 已成功生成。0 个警告，0 个错误。
```

**新引入问题检查** ✅ 未发现新问题
- 所有修复均为局部修改，未改变公共 API 签名（除 M4 的重命名）
- 事件绑定/解绑逻辑完整，无泄漏
- 状态机转换守卫条件未被破坏
- 设计文档与实现一致性已恢复

### 总结

| 指标 | 结果 |
|------|------|
| Critical 修复 | 3/3 ✅ |
| Major 修复 | 7/7 ✅ |
| 额外修复 | 3/3 ✅ |
| 编译 | ✅ 通过 |
| 回归问题 | 无 |
| 验证结论 | ✅ 全部通过，可投入使用 |
