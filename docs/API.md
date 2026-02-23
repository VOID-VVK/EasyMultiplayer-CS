# EasyMultiplayer API 参考

> 完整的公开 API 文档，涵盖所有类、接口、方法、属性和信号。

---

## 目录

- [枚举类型](#枚举类型)
- [配置类](#配置类)
- [EasyMultiplayer（核心单例）](#easymultiplayer核心单例)
- [ITransport 接口](#itransport-接口)
- [ENetTransport](#enettransport)
- [IDiscovery 接口](#idiscovery-接口)
- [UdpBroadcastDiscovery](#udpbroadcastdiscovery)
- [RoomHost](#roomhost)
- [RoomClient](#roomclient)
- [HeartbeatManager](#heartbeatmanager)
- [MessageChannel](#messagechannel)
- [数据类](#数据类)

---

## 枚举类型

### ConnectionState

`namespace EasyMultiplayer.Core`

EasyMultiplayer 连接生命周期状态。

| 值 | 说明 |
|----|------|
| `Disconnected` | 初始状态或已断开连接 |
| `Hosting` | 作为主机等待客户端连接 |
| `Joining` | 客户端正在连接到主机 |
| `Connected` | 已连接，双方在线 |
| `Reconnecting` | 对端断线，等待重连中 |

状态转换：
```
Disconnected ──Host()──► Hosting ──PeerJoined──► Connected
Disconnected ──Join()──► Joining ──ConnSucceeded──► Connected
Connected ──心跳超时──► Reconnecting ──重连成功──► Connected
Reconnecting ──超时/失败──► Disconnected
Any ──Disconnect()──► Disconnected
```

### NetQuality

`namespace EasyMultiplayer.Core`

网络质量分级，基于 RTT 评估。

| 值 | 说明 |
|----|------|
| `Good` | RTT < 100ms |
| `Warning` | 100ms ≤ RTT < 300ms |
| `Bad` | RTT ≥ 300ms 或断线 |

### TransportStatus

`namespace EasyMultiplayer.Transport`

传输层状态。

| 值 | 说明 |
|----|------|
| `Disconnected` | 未连接 |
| `Connecting` | 正在连接中 |
| `Connected` | 已连接 |

### RoomState

`namespace EasyMultiplayer.Room`

房间主机状态。

| 值 | 说明 |
|----|------|
| `Idle` | 空闲，未创建房间 |
| `Waiting` | 等待客人加入 |
| `Ready` | 客人已加入，准备阶段 |
| `Playing` | 游戏进行中 |
| `Closed` | 房间已关闭 |

状态转换：
```
Idle ──CreateRoom()──► Waiting ──PeerJoined──► Ready ──StartGame()──► Playing
Ready ──PeerLeft(无客人)──► Waiting
Any ──CloseRoom()──► Closed
```

### ClientState

`namespace EasyMultiplayer.Room`

房间客户端状态。

| 值 | 说明 |
|----|------|
| `Idle` | 空闲 |
| `Searching` | 正在搜索房间 |
| `Joining` | 正在加入房间 |
| `InRoom` | 已在房间中 |
| `GameStarting` | 游戏即将开始 |

状态转换：
```
Idle ──StartSearching()──► Searching ──JoinRoom()──► Joining ──成功──► InRoom
Idle ──JoinRoom()──► Joining（手动输入 IP）
InRoom ──GameStart──► GameStarting
InRoom ──LeaveRoom()/断开──► Idle
Joining ──失败──► Idle
```

---

## 配置类

### EasyMultiplayerConfig

`namespace EasyMultiplayer.Core` · 继承 `Godot.Resource`

所有可配置参数，支持 Inspector 编辑和 `.tres` 文件持久化。

#### 属性

| 属性 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Port` | `int` | `27015` | ENet 监听/连接端口 |
| `MaxClients` | `int` | `1` | 最大客户端数 |
| `HeartbeatInterval` | `float` | `3.0` | 心跳 Ping 发送间隔（秒） |
| `DisconnectTimeout` | `float` | `10.0` | 判定对端断线的超时阈值（秒） |
| `ReconnectTimeout` | `float` | `30.0` | Host 端等待对端重连的上限时间（秒） |
| `MaxReconnectAttempts` | `int` | `20` | Client 端最大重连尝试次数 |
| `ReconnectRetryInterval` | `float` | `3.0` | Client 端每次重连尝试的间隔（秒） |
| `RpcMinIntervalMs` | `double` | `100.0` | 消息通道每通道最小发送间隔（毫秒），0 = 不限制 |
| `FallbackCheckInterval` | `float` | `10.0` | 兜底连接检查间隔（秒） |
| `FallbackGracePeriod` | `float` | `5.0` | 进入场景后的兜底检查宽限期（秒） |
| `BroadcastPort` | `int` | `27016` | UDP 广播端口 |
| `BroadcastInterval` | `float` | `1.0` | 广播发送间隔（秒） |
| `RoomTimeout` | `double` | `5.0` | 房间超时移除阈值（秒） |

---

## EasyMultiplayer（核心单例）

`namespace EasyMultiplayer.Core` · 继承 `Godot.Node` · Autoload 单例

统一 API 入口，组合所有模块。管理连接生命周期、版本校验、主动退出通知。

### 属性

| 属性 | 类型 | 说明 |
|------|------|------|
| `Config` | `EasyMultiplayerConfig` | 插件配置资源（`[Export]`） |
| `GameVersion` | `string` | 游戏版本号，连接时自动交换校验，默认 `"1.0.0"` |
| `State` | `ConnectionState` | 当前连接状态（只读） |
| `IsServer` | `bool` | 是否为服务端 |
| `UniqueId` | `int` | 本机唯一标识符 |
| `IsNetworkConnected` | `bool` | 是否已连接（Connected 或 Hosting） |
| `IsReconnecting` | `bool` | 是否正在重连 |
| `ConnectedPeers` | `IReadOnlyCollection<int>` | 已连接的对端 ID 列表 |
| `Transport` | `ITransport` | 传输层实例 |
| `Discovery` | `IDiscovery` | 发现层实例 |
| `Heartbeat` | `HeartbeatManager` | 心跳管理器 |
| `MessageChannel` | `MessageChannel` | 消息通道 |
| `RoomHost` | `RoomHost` | 房间主机 |
| `RoomClient` | `RoomClient` | 房间客户端 |

### 方法

#### `Host(int port = -1, int maxClients = -1) → Error`

作为主机开始监听。

- `port`：监听端口，-1 使用配置值
- `maxClients`：最大客户端数，-1 使用配置值
- 返回：`Error.Ok` 成功，`Error.AlreadyInUse` 当前状态不是 Disconnected

#### `Join(string address, int port = -1) → Error`

作为客户端连接到主机。

- `address`：主机 IP 地址
- `port`：主机端口，-1 使用配置值
- 返回：`Error.Ok` 成功，`Error.AlreadyInUse` 当前状态不是 Disconnected

#### `Disconnect()`

断开连接并重置所有状态。停止心跳、广播、监听，清理所有对端。

#### `CreateRoom(string roomName, string gameType, int port = -1) → Error`

创建房间（快捷方法，内部调用 `RoomHost.CreateRoom`）。

#### `JoinRoom(string hostIp, int port = -1) → Error`

加入房间（快捷方法，内部调用 `RoomClient.JoinRoom`）。

#### `SendMessage(int peerId, string channel, byte[] data)`

发送可靠消息给指定对端。

#### `SendMessage(int peerId, string channel, string data)`

发送可靠消息（string 重载，自动 UTF-8 编码）。

#### `BroadcastMessage(string channel, byte[] data, bool reliable = true)`

广播消息给所有对端。

#### `GracefulDisconnect(string reason = "quit")`

主动退出：先发通知再延迟 200ms 断开，让对端区分主动退出与意外断线。

- `reason`：退出原因，如 `"quit"`、`"room"`、`"game"`

### 信号

| 信号 | 参数 | 说明 |
|------|------|------|
| `StateChanged` | `int oldState, int newState` | 连接状态转换 |
| `PeerJoined` | `int peerId` | 对端连接成功 |
| `PeerLeft` | `int peerId` | 对端断开连接 |
| `ConnectionSucceeded` | — | Client 连接 Host 成功 |
| `ConnectionFailed` | — | Client 连接 Host 失败 |
| `VersionVerified` | `string remoteVersion` | 版本校验通过 |
| `VersionMismatch` | `string localVersion, string remoteVersion` | 版本不匹配 |
| `PeerGracefulQuit` | `int peerId, string reason` | 对端主动退出 |
| `FullSyncRequested` | `int peerId` | 重连成功后需要全量同步 |

---

## ITransport 接口

`namespace EasyMultiplayer.Transport`

传输层抽象接口。所有网络传输实现（ENet、WebSocket 等）均需实现此接口。

### 属性

| 属性 | 类型 | 说明 |
|------|------|------|
| `IsServer` | `bool` | 当前是否为服务端 |
| `UniqueId` | `int` | 本机唯一标识符 |
| `Status` | `TransportStatus` | 当前传输层状态 |

### 方法

#### `CreateHost(int port, int maxClients) → Error`

创建主机（服务端），开始监听指定端口。

#### `CreateClient(string address, int port) → Error`

创建客户端，连接到指定主机。

#### `Disconnect()`

断开所有连接并释放资源。

#### `DisconnectPeer(int peerId)`

断开指定对端的连接。

#### `Poll()`

每帧调用，驱动内部事件循环。

#### `SendReliable(int peerId, int channel, byte[] data)`

通过可靠通道发送数据。保证送达且按序。

#### `SendUnreliable(int peerId, int channel, byte[] data)`

通过不可靠通道发送数据。不保证送达，适合高频低优先数据。

### 事件

| 事件 | 签名 | 说明 |
|------|------|------|
| `PeerConnected` | `Action<int>` | 对端连接成功，参数为对端 ID |
| `PeerDisconnected` | `Action<int>` | 对端断开连接，参数为对端 ID |
| `DataReceived` | `Action<int, int, byte[]>` | 收到数据：对端 ID、通道编号、数据载荷 |
| `ConnectionSucceeded` | `Action` | 客户端连接主机成功 |
| `ConnectionFailed` | `Action` | 客户端连接主机失败 |

---

## ENetTransport

`namespace EasyMultiplayer.Transport` · 实现 `ITransport`

基于 Godot `ENetMultiplayerPeer` 的默认传输层实现。

### 架构要点

- **不设置 MultiplayerPeer 到 MultiplayerAPI** — 避免 Godot RPC 系统干扰
- **握手包驱动 peer 发现** — 客户端连接后发送握手包（`HandshakeChannel = int.MinValue`），服务器通过握手包或数据包 `senderId` 发现新 peer
- **客户端通过 `GetConnectionStatus()` 轮询检测连接** — 不依赖 Godot 信号
- **数据包格式** — `[channel:4bytes(int32)][data:N bytes]`

### 额外属性

| 属性 | 类型 | 说明 |
|------|------|------|
| `LastAddress` | `string` | 上次连接的地址（用于重连） |
| `LastPort` | `int` | 上次连接的端口（用于重连） |

### 额外方法

#### `Initialize(SceneTree sceneTree)`

保留接口兼容性，内部不使用 SceneTree。

#### `Cleanup()`

清理资源，清空已知 peer 集合。

### Peer 发现机制

服务器端通过三层兜底发现客户端：

1. **ENet 信号**（`OnENetPeerConnected`）— 首选路径
2. **握手包**（`HandshakeChannel`）— 客户端连接后主动发送，服务器收到后触发 `PeerConnected`
3. **首包兜底** — 服务器收到未知 peer 的普通数据包时，也触发 `PeerConnected`

---

## IDiscovery 接口

`namespace EasyMultiplayer.Discovery`

房间发现层抽象接口。

### 属性

| 属性 | 类型 | 说明 |
|------|------|------|
| `IsBroadcasting` | `bool` | 当前是否正在广播 |
| `IsListening` | `bool` | 当前是否正在监听 |
| `Rooms` | `IReadOnlyDictionary<string, DiscoveredRoom>` | 当前发现的房间列表，键为 `"ip:port"` |

### 方法

#### `StartBroadcast(RoomInfo info)`

开始广播房间信息。

#### `StopBroadcast()`

停止广播。

#### `StartListening()`

开始监听房间广播。

#### `StopListening()`

停止监听。

### 事件

| 事件 | 签名 | 说明 |
|------|------|------|
| `RoomFound` | `Action<DiscoveredRoom>` | 发现新房间 |
| `RoomLost` | `Action<string>` | 房间超时消失，参数为房间键 `"ip:port"` |
| `RoomListUpdated` | `Action` | 房间列表发生变化 |

---

## UdpBroadcastDiscovery

`namespace EasyMultiplayer.Discovery` · 继承 `Godot.Node` · 实现 `IDiscovery`

基于 UDP 广播的默认房间发现实现。

### 特性

- Magic 标识 `EASYMULTI_V1`，与千棋世界的 `QIANQI_V1` 区分
- 使用 `InstanceId`（`Guid` 前 8 位）过滤自身广播，支持同机多实例测试
- 通过 `System.Net.NetworkInformation.NetworkInterface` 获取本机 IP，避免 DNS 解析失败
- 支持后台线程预加载本机 IP（`PreloadLocalIps()`）
- 异步接收回调通过 `CallDeferred` 切回主线程，确保线程安全

### 额外方法

#### `SetConfig(EasyMultiplayerConfig config)`

设置配置。应在使用前调用。

#### `PreloadLocalIps()`

在后台线程预加载本机 IP，避免 `StartListening` 首次调用时阻塞主线程。

---

## RoomHost

`namespace EasyMultiplayer.Room` · 继承 `Godot.Node`

房间主机逻辑。负责创建房间、广播、等待客人加入、管理准备状态和开始游戏。

### 属性

| 属性 | 类型 | 说明 |
|------|------|------|
| `State` | `RoomState` | 当前房间状态 |
| `RoomName` | `string` | 房间名称 |
| `GameType` | `string` | 游戏类型标识 |
| `Port` | `int` | 房间端口 |
| `MaxPlayers` | `int` | 最大玩家数（含 Host） |
| `HostReady` | `bool` | 房主准备状态 |
| `GuestPeerIds` | `IReadOnlyCollection<int>` | 当前客人 peer ID 列表 |
| `PlayerCount` | `int` | 当前玩家数（含 Host） |

### 方法

#### `Setup(ITransport transport, IDiscovery discovery, MessageChannel messageChannel, EasyMultiplayerConfig config, string gameVersion = "1.0.0")`

设置依赖。应在使用前调用。

#### `CreateRoom(string name, string gameType, int port = -1, int maxPlayers = -1) → Error`

创建房间并开始广播。

- `name`：房间名称
- `gameType`：游戏类型标识
- `port`：监听端口，-1 使用配置值
- `maxPlayers`：最大玩家数（含 Host），-1 使用 `Config.MaxClients + 1`

#### `StopBroadcast()`

仅停止广播，不断开已连接的客人。用于对手加入后停止广播同时保持连接。

#### `CloseRoom()`

关闭房间，断开所有客人，清理所有资源。

#### `SetHostReady(bool ready)`

设置房主准备状态，并通知所有客人。仅在 `Ready` 状态有效。

#### `StartGame()`

开始游戏。仅在所有人都准备就绪时有效。

#### `ResetReadyState()`

重置所有准备状态（Host 和所有 Guest）。

#### `IsGuestReady(int peerId) → bool`

检查指定客人是否已准备。

#### `AreAllGuestsReady() → bool`

检查是否所有客人都已准备。

### 信号

| 信号 | 参数 | 说明 |
|------|------|------|
| `RoomStateChanged` | `int oldState, int newState` | 房间状态转换 |
| `GuestJoined` | `int peerId` | 客人加入房间 |
| `GuestLeft` | `int peerId` | 客人离开房间 |
| `GuestReadyChanged` | `int peerId, bool ready` | 客人准备状态变更 |
| `AllReady` | — | 所有人（Host + 全部 Guest）都已准备 |
| `GameStarting` | `string gameType` | 游戏即将开始 |

---

## RoomClient

`namespace EasyMultiplayer.Room` · 继承 `Godot.Node`

房间客户端逻辑。负责搜索房间、加入房间、管理准备状态。

### 属性

| 属性 | 类型 | 说明 |
|------|------|------|
| `State` | `ClientState` | 当前客户端状态 |
| `CurrentRoom` | `RoomInfo?` | 当前加入的房间信息 |
| `CurrentHostIp` | `string` | 当前房间的 Host IP |
| `IsReady` | `bool` | 本地准备状态 |
| `HostReady` | `bool` | 房主准备状态 |

### 方法

#### `Setup(ITransport transport, IDiscovery discovery, MessageChannel messageChannel)`

设置依赖。应在使用前调用。

#### `StartSearching()`

开始搜索局域网房间。仅在 `Idle` 状态有效。

#### `StopSearching()`

停止搜索。

#### `GetDiscoveredRooms() → IReadOnlyDictionary<string, DiscoveredRoom>?`

获取当前发现的房间列表。

#### `JoinRoom(string hostIp, int port) → Error`

加入指定房间。

- `hostIp`：主机 IP 地址
- `port`：主机端口

#### `LeaveRoom()`

离开当前房间。

#### `SetReady(bool ready)`

设置准备状态并通知 Host。仅在 `InRoom` 状态有效。

### 信号

| 信号 | 参数 | 说明 |
|------|------|------|
| `ClientStateChanged` | `int oldState, int newState` | 客户端状态转换 |
| `JoinSucceeded` | `string roomName, string gameType` | 成功加入房间 |
| `JoinFailed` | `string reason` | 加入房间失败 |
| `HostReadyChanged` | `bool ready` | 房主准备状态变更 |
| `GameStarting` | `string gameType` | 收到游戏开始通知 |
| `DisconnectedFromRoom` | `string reason` | 与房间断开连接 |

---

## HeartbeatManager

`namespace EasyMultiplayer.Heartbeat` · 继承 `Godot.Node`

心跳管理器。负责 Ping/Pong 心跳检测、RTT 计算、网络质量分级、断线检测和客户端自动重连。

### 协议

- 心跳通道：`channel = 255`
- Ping 包：`0x01`
- Pong 包：`0x02`
- 使用 Unreliable 通道发送，避免阻塞可靠通道

### 属性

| 属性 | 类型 | 说明 |
|------|------|------|
| `RttMs` | `double` | 当前 RTT（毫秒），-1 表示未测量 |
| `Quality` | `NetQuality` | 当前网络质量 |
| `ReconnectElapsed` | `double` | 重连已等待的秒数 |

### 方法

#### `Setup(ITransport transport, EasyMultiplayerConfig config)`

设置传输层和配置。应在使用前调用。

#### `Start()`

启动心跳检测。在连接建立后调用。

#### `Stop()`

停止心跳检测。

#### `TrackPeer(int peerId)`

添加需要跟踪心跳的对端。

#### `UntrackPeer(int peerId)`

移除对端跟踪。

#### `Reset()`

重置所有状态（停止心跳、取消重连、清空跟踪列表）。

#### `CancelReconnect()`

强制取消重连等待。

### 信号

| 信号 | 参数 | 说明 |
|------|------|------|
| `NetQualityChanged` | `int quality, double rttMs` | 网络质量等级变化 |
| `PeerTimedOut` | `int peerId` | 对端心跳超时 |
| `PeerReconnected` | `int peerId` | 对端重连成功 |
| `ReconnectTimedOut` | — | Host 端重连等待超时 |
| `ReconnectFailed` | — | Client 端自动重连全部失败 |
| `FullSyncRequested` | `int peerId` | 重连成功后需要全量同步 |

### 重连机制

- **Host 端**：对端断线后进入等待状态，等待 `ReconnectTimeout` 秒。对端重新连接后触发 `PeerReconnected` + `FullSyncRequested`。
- **Client 端**：自动重连，最多尝试 `MaxReconnectAttempts` 次，每次间隔 `ReconnectRetryInterval` 秒。仅支持 `ENetTransport`（使用 `LastAddress` / `LastPort`）。

---

## MessageChannel

`namespace EasyMultiplayer.Core` · 继承 `Godot.Node`

通用消息通道。提供统一的消息收发接口，替代散落的业务 RPC。

### 消息包格式

```
[channelNameLength:2bytes(uint16)][channelName:UTF8][data:N bytes]
```

### 属性

| 属性 | 类型 | 说明 |
|------|------|------|
| `RpcMinIntervalMs` | `double` | 每通道最小发送间隔（毫秒），0 = 不限制 |

### 方法

#### `SetTransport(ITransport transport)`

设置传输层引用。应在使用前调用。

#### `SendReliable(int peerId, string channel, byte[] data)`

发送可靠消息。保证送达且按序。

#### `SendReliable(int peerId, string channel, string data)`

发送可靠消息（string 重载，自动 UTF-8 编码）。

#### `SendUnreliable(int peerId, string channel, byte[] data)`

发送不可靠消息。不保证送达，适合高频低优先数据。

#### `Broadcast(string channel, byte[] data, bool reliable = true)`

广播消息给所有已连接对端（`peerId = 0`）。

#### `ResetRateLimits()`

重置频率限制计时器。

### 信号

| 信号 | 参数 | 说明 |
|------|------|------|
| `MessageReceived` | `int peerId, string channel, byte[] data` | 收到消息 |

### 内置通道

以下通道由插件内部使用，使用者应避免使用这些通道名：

| 通道 | 用途 |
|------|------|
| `__version` | 版本校验（EasyMultiplayer 内部） |
| `__quit` | 主动退出通知（EasyMultiplayer 内部） |
| `__room_ctrl` | 房间控制消息（RoomHost / RoomClient 内部） |

---

## 数据类

### RoomInfo

`namespace EasyMultiplayer.Discovery`

房间信息数据类，作为广播载荷在网络中传输。

| 属性 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Magic` | `string` | `"EASYMULTI_V1"` | 广播魔数，用于过滤非本插件的广播包 |
| `HostName` | `string` | `""` | 房间名称 / 主机名 |
| `GameType` | `string` | `""` | 游戏类型标识 |
| `PlayerCount` | `int` | `1` | 当前玩家数 |
| `MaxPlayers` | `int` | `2` | 最大玩家数 |
| `Port` | `int` | `27015` | 游戏端口 |
| `Version` | `string` | `"1.0.0"` | 游戏版本号 |
| `InstanceId` | `string` | `""` | 广播实例标识，用于过滤自身广播 |
| `Metadata` | `Dictionary<string, string>` | `new()` | 自定义元数据字典 |

### DiscoveredRoom

`namespace EasyMultiplayer.Discovery`

发现的房间条目。

| 属性 | 类型 | 说明 |
|------|------|------|
| `Info` | `RoomInfo` | 房间信息 |
| `HostIp` | `string` | 主机 IP 地址 |
| `LastSeen` | `double` | 最后一次收到该房间广播的时间戳（引擎时间） |
