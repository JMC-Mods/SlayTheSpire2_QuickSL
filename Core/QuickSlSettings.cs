using Godot;
using JmcModLib.Config.UI;

namespace QuickSL.Core;

public static class QuickSlSettings
{
    private const string KeybindGroup = "keybinds";
    private const string QuickSlConfigKey = "keybind.quick_sl";
    private const ulong QuickSlDebounceMs = 1000UL;

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
