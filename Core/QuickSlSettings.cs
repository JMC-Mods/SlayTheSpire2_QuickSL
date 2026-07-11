using Godot;
using JmcModLib.Config;
using JmcModLib.Config.UI;
using JmcModLib.Multiplayer;
using JmcModLib.UI.PauseMenu;
using JmcModLib.Utils;

namespace QuickSL.Core;

public static class QuickSlSettings
{
    private const string GeneralGroup = "general";
    private const string KeybindGroup = "keybinds";
    private const string MultiplayerGroup = "multiplayer";
    private const string QuickSlConfigKey = "keybind.quick_sl";
    private const string PauseMenuButtonKey = "quick_sl.pause_menu.retry";
    private const ulong QuickSlDebounceMs = 1000UL;

    [UIToggle]
    [Config(
        "快速模式",
        group: GeneralGroup,
        Description = "开启后快速 SL 会跳过载入前后的淡入淡出动画；关闭后保持当前动画效果。",
        Key = "general.fast_mode",
        Order = 5)]
    public static bool FastMode = true;

    [OptionalNetworkFeature(
        QuickSlMultiplayerFeature.FeatureId,
        typeof(IQuickSlNetworkMessage),
        CompatibilityVersion = QuickSlMultiplayerFeature.CompatibilityVersion)]
    [UIToggle]
    [Config(
        "启用多人快速 SL",
        group: MultiplayerGroup,
        Description = "启用快速 SL 的多人联机功能。联机期间修改此选项，需要完全断开联机后才会生效。",
        Key = "multiplayer.enabled",
        Order = 5,
        RestartRequired = false)]
    public static bool EnableMultiplayerQuickSl = true;

    [UIVisibleWhen(nameof(EnableMultiplayerQuickSl))]
    [UIToggle]
    [Config(
        "发起多人 SL 时询问客机",
        group: MultiplayerGroup,
        Description = "作为主机发起多人快速 SL 时，先弹窗询问所有已连接客机；关闭后会直接通知客机同步 SL。",
        Key = "multiplayer.require_client_confirmation",
        Order = 10)]
    public static bool RequireMultiplayerClientConfirmation = true;

    [UIVisibleWhen(nameof(EnableMultiplayerQuickSl))]
    [UIToggle]
    [Config(
        "允许客机发起多人 SL",
        group: MultiplayerGroup,
        Description = "作为主机时，允许客机按下快速 SL 热键来请求同步快速 SL；关闭后客机请求会被直接拒绝。",
        Key = "multiplayer.allow_client_initiated_sl",
        Order = 15)]
    public static bool AllowClientInitiatedQuickSl = true;

    [UIVisibleWhen(nameof(EnableMultiplayerQuickSl))]
    [UIToggle]
    [Config(
        "客机发起多人 SL 时询问主机",
        group: MultiplayerGroup,
        Description = "客机按下快速 SL 热键时，先弹窗询问主机是否同意；关闭后主机会直接进入客机确认流程。",
        Key = "multiplayer.require_host_confirmation",
        Order = 20)]
    public static bool RequireMultiplayerHostConfirmation = true;

    [UIHotkey(
        "快速 SL",
        group: KeybindGroup,
        Key = QuickSlConfigKey,
        Description = "重新载入当前局的存档，效果等同于保存并退出后继续游戏。",
        DefaultKeyboard = Key.F5,
        AllowController = true,
        DebounceMs = QuickSlDebounceMs,
        Order = 10)]
    public static void QuickSl()
    {
        QuickSlService.RequestQuickSl();
    }

    [PauseMenuButton("S & L", Key = PauseMenuButtonKey, Order = 10)]
    public static async Task QuickSlFromPauseMenu()
    {
        ModLogger.Trace("快速 SL：通过暂停菜单入口触发。");
        await QuickSlService.RequestQuickSlAsync();
    }
}
