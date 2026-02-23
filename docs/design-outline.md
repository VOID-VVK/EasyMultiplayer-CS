# EasyMultiplayer 设计文档大纲

> Godot 4.x + C# 纯网络层轻量插件，不含 UI。
> 目录：`addons/EasyMultiplayer/`

---

## 1. 概述与目标

- 从千棋世界项目提炼的通用局域网多人插件，支持 2~N 人（MaxClients 可配置）
- 纯网络层：连接管理、房间发现、心跳、断线重连、版本校验、通用消息通道
- 不依赖外部 EventBus，所有事件通过插件自身 Godot Signal 暴露
- 传输层与发现层均可插拔，便于未来扩展 WebSocket / Steam / 大厅服务器

## 2. 架构总览

- 分层：Application → Plugin API → Transport Abstraction → Concrete Transport
- 核心节点：`EasyMultiplayer`（Autoload 单例），统一入口
- 接口抽象：`ITransport`（传输层）、`IDiscovery`（房间发现）
- 内置实现：`ENetTransport`、`UdpBroadcastDiscovery`
- 插件目录结构一览（见第 10 章）

## 3. ITransport 接口设计

- 方法：`CreateHost(port, maxClients)`, `CreateClient(address, port)`, `Disconnect()`, `DisconnectPeer(peerId)`
- 方法：`SendReliable(peerId, channel, data)`, `SendUnreliable(peerId, channel, data)`, `Poll()`
- 事件回调：`OnPeerConnected`, `OnPeerDisconnected`, `OnDataReceived`, `OnConnectionSucceeded`, `OnConnectionFailed`
- 属性：`IsServer`, `UniqueId`, `ConnectionStatus`
- ENetTransport 实现要点：封装 ENetMultiplayerPeer，映射 Godot Multiplayer 回调

## 4. IDiscovery 接口设计

- 方法：`StartBroadcast(RoomInfo)`, `StopBroadcast()`, `StartListening()`, `StopListening()`
- 事件回调：`OnRoomFound(DiscoveredRoom)`, `OnRoomLost(string key)`, `OnRoomListUpdated()`
- `RoomInfo` 数据结构：Magic、HostName、GameType、PlayerCount、MaxPlayers、Port、Version、自定义 Metadata（Dictionary）
- UdpBroadcastDiscovery 实现要点：System.Net.Sockets.UdpClient，广播间隔/超时可配置，CallDeferred 回主线程

## 5. 连接管理与状态机

- 状态枚举 `ConnectionState`：Disconnected → Hosting / Joining → Connected → Reconnecting → Disconnected
- 状态转换规则与守卫条件（每个转换的前置检查）
- 公共 API：`Host(port)`, `Join(address, port)`, `Disconnect()`, `CancelReconnect()`
- 信号：`StateChanged`, `PeerJoined`, `PeerLeft`, `ConnectionSucceeded`, `ConnectionFailed`

## 6. 房间系统

- `RoomHost`：创建房间 → 广播 → 等待加入 → 准备 → 开始；关闭房间时停止广播并断开
- `RoomClient`：搜索 → 加入 → 准备；离开时清理状态
- 房间状态机：Idle → Waiting → Ready → Playing → Closed（Host 端）
- 客户端状态机：Idle → Searching → Joining → InRoom → GameStarting
- 信号：`GuestJoined`, `GuestLeft`, `ReadyChanged`, `AllReady`, `GameStarting`, `JoinSucceeded`, `JoinFailed`

## 7. 心跳检测与网络质量

- Ping/Pong 机制：可配置间隔（默认 3s），Unreliable 通道发送
- 断线判定：超过 DisconnectTimeout（默认 10s）未收到 Pong 则触发断线流程
- RTT 计算与网络质量分级：Good（<100ms）、Warning（100~300ms）、Bad（>300ms）
- 信号：`NetQualityChanged(quality, rttMs)`，`PeerTimedOut(peerId)`

## 8. 断线重连

- Host 端：进入 Reconnecting 状态，启动重连计时器，超时后发出 `ReconnectTimedOut`
- Client 端：自动重连（可配置 MaxAttempts 默认 20，RetryInterval 默认 3s），逐次重试
- 重连成功后恢复心跳，发出 `PeerReconnected` 信号
- 全部重试失败 → 发出 `ReconnectFailed`；用户可随时 `CancelReconnect()`
- 重连期间的消息缓冲策略（可选，标注为 v2 扩展）

## 9. 版本校验

- 连接建立后 Client 发送版本号，Host 校验
- 匹配 → Host 回复确认；不匹配 → Host 发送 `VersionMismatch` 后延迟踢出
- 信号：`VersionVerified`, `VersionMismatch(localVer, remoteVer)`
- 版本号由使用者通过 `EasyMultiplayer.GameVersion` 属性设置

## 10. 通用消息通道

- 替代千棋世界中散落的业务 RPC，提供统一的 `SendMessage(peerId, channel, data)` 接口
- channel：string 标识（如 "move", "chat", "sync"），插件不解析内容，只负责投递
- data：`byte[]` 或 `string`，由使用者自行序列化
- 信号：`MessageReceived(peerId, channel, data)`
- 支持 Reliable / Unreliable 两种模式
- RPC 频率限制：可配置最小间隔（默认 100ms），防刷

## 11. 兜底机制（强制）

- 所有等待状态（Joining、Reconnecting、搜索房间）必须有超时，超时后自动回退到安全状态
- 所有超时值可配置，提供合理默认值
- `CancellationToken` 模式：长时间操作返回可取消句柄
- 主动退出通知：`SendGracefulQuit()` 先发消息再延迟断开，对端区分主动退出与意外断线
- 状态重置：每次进入新阶段时重置所有相关状态变量
- 信号断开：节点 `_ExitTree` 时正确清理所有信号连接和网络资源

## 12. 配置与导出

- `EasyMultiplayerConfig` Resource：所有可调参数集中管理
  - Port、MaxClients、HeartbeatInterval、DisconnectTimeout、ReconnectTimeout
  - MaxReconnectAttempts、ReconnectRetryInterval、RpcMinInterval
  - BroadcastPort、BroadcastInterval、RoomTimeout
- 支持 EditorInspector 直接编辑，也可代码设置
- `plugin.cfg` 注册信息

## 13. 插件目录结构

```
addons/EasyMultiplayer/
├── plugin.cfg
├── EasyMultiplayerPlugin.cs          # EditorPlugin 入口
├── Core/
│   ├── EasyMultiplayer.cs            # Autoload 单例，统一 API
│   ├── ConnectionState.cs            # 状态枚举
│   ├── EasyMultiplayerConfig.cs      # 配置 Resource
│   └── MessageChannel.cs             # 通用消息通道
├── Transport/
│   ├── ITransport.cs                 # 传输层接口
│   └── ENetTransport.cs              # ENet 实现
├── Discovery/
│   ├── IDiscovery.cs                 # 发现层接口
│   ├── RoomInfo.cs                   # 房间信息数据结构
│   └── UdpBroadcastDiscovery.cs      # UDP 广播实现
├── Room/
│   ├── RoomHost.cs                   # 房间主机逻辑
│   └── RoomClient.cs                 # 房间客户端逻辑
└── Heartbeat/
    └── HeartbeatManager.cs           # 心跳 & RTT & 重连计时
```

## 14. 信号一览表

- 列出所有公开信号的名称、参数、触发时机
- 按模块分组：连接、房间、心跳、消息、兜底
- 与千棋世界 EventBus 信号的对应关系映射表（迁移参考）

## 15. 迁移指南（从千棋世界到 EasyMultiplayer）

- NetworkManager → EasyMultiplayer 单例 + ENetTransport
- RoomDiscovery → UdpBroadcastDiscovery（实现 IDiscovery）
- RoomHost / RoomClient → Room 模块（API 基本保持一致）
- 业务 RPC（走子、悔棋、认输等）→ 通用消息通道 `SendMessage("move", data)`
- EventBus 依赖 → 插件 Signal 直连

## 16. 未来扩展（v2+）

- WebSocketTransport：实现 ITransport，用于跨网段 / Web 平台
- SteamTransport：Steam Networking Sockets 封装
- LobbyServerDiscovery：实现 IDiscovery，中心化房间列表
- 消息缓冲与重放：断线期间缓存消息，重连后按序重放
- NAT 穿透 / Relay 支持
