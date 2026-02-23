namespace EasyMultiplayer.Room;

/// <summary>
/// 房间主机状态枚举。
/// </summary>
/// <remarks>
/// 状态转换规则：
/// <list type="bullet">
///   <item>Idle → Waiting：调用 CreateRoom()</item>
///   <item>Waiting → Ready：有客人加入（PeerJoined）</item>
///   <item>Ready → Playing：调用 StartGame()</item>
///   <item>Ready → Waiting：客人离开（PeerLeft）</item>
///   <item>Any → Closed：调用 CloseRoom()</item>
///   <item>Closed → Idle：可重新创建房间</item>
/// </list>
/// </remarks>
public enum RoomState
{
    /// <summary>空闲，未创建房间。</summary>
    Idle,

    /// <summary>等待客人加入。</summary>
    Waiting,

    /// <summary>客人已加入，准备阶段。</summary>
    Ready,

    /// <summary>游戏进行中。</summary>
    Playing,

    /// <summary>房间已关闭。</summary>
    Closed
}

/// <summary>
/// 房间客户端状态枚举。
/// </summary>
/// <remarks>
/// 状态转换规则：
/// <list type="bullet">
///   <item>Idle → Searching：调用 StartSearching()</item>
///   <item>Searching → Joining：调用 JoinRoom()</item>
///   <item>Idle → Joining：直接调用 JoinRoom()（手动输入 IP）</item>
///   <item>Joining → InRoom：连接成功</item>
///   <item>Joining → Idle：连接失败</item>
///   <item>InRoom → GameStarting：收到游戏开始通知</item>
///   <item>InRoom → Idle：调用 LeaveRoom() 或断开连接</item>
///   <item>GameStarting → Idle：断开连接</item>
/// </list>
/// </remarks>
public enum ClientState
{
    /// <summary>空闲。</summary>
    Idle,

    /// <summary>正在搜索房间。</summary>
    Searching,

    /// <summary>正在加入房间。</summary>
    Joining,

    /// <summary>已在房间中。</summary>
    InRoom,

    /// <summary>游戏即将开始。</summary>
    GameStarting
}
