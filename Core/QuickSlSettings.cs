using Godot;
using JmcModLib.Config;
using JmcModLib.Config.UI;

namespace QuickSL.Core;

public static class QuickSlSettings
{
    private const string KeybindGroup = "keybinds";
    private const string MultiplayerGroup = "multiplayer";
    private const string QuickSlConfigKey = "keybind.quick_sl";
    private const ulong QuickSlDebounceMs = 1000UL;

    [UIToggle]
    [Config(
        "发起多人 SL 时询问客机",
        group: MultiplayerGroup,
        Description = "作为主机发起多人快速 SL 时，先弹窗询问所有已连接客机；关闭后会直接通知客机同步 SL。",
        Key = "multiplayer.require_client_confirmation",
        Order = 10)]
    public static bool RequireMultiplayerClientConfirmation = true;

    [UIHotkey(
        "快速 SL",
        group: KeybindGroup,
        Key = QuickSlConfigKey,
        Description = "重新载入当前局的存档，效果等同于保存并退出后继续游戏。",
        DefaultKeyboard = Key.F5,
        ConsumeInput = true,
        ExactModifiers = true,
        DebounceMs = QuickSlDebounceMs,
        Order = 10)]
    public static void QuickSl()
    {
        QuickSlService.RequestQuickSl();
    }
}
