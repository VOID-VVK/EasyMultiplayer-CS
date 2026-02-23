#if TOOLS
using Godot;

namespace EasyMultiplayer;

/// <summary>
/// EasyMultiplayer 编辑器插件入口。
/// 负责插件的启用和禁用生命周期管理。
/// </summary>
[Tool]
public partial class EasyMultiplayerPlugin : EditorPlugin
{
    /// <summary>
    /// 插件启用时调用。
    /// </summary>
    public override void _EnterTree()
    {
        GD.Print("[EasyMultiplayer] Plugin enabled.");
    }

    /// <summary>
    /// 插件禁用时调用。
    /// </summary>
    public override void _ExitTree()
    {
        GD.Print("[EasyMultiplayer] Plugin disabled.");
    }
}
#endif
