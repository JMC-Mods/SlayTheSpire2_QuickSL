using JmcModLib.Multiplayer;
using JmcModLib.Utils;

namespace QuickSL.Core;

internal static class QuickSlMultiplayerFeature
{
    internal const string FeatureId = "quick-sl.multiplayer";
    internal const string CompatibilityVersion = "1";

    private static OptionalNetworkFeatureHandle? handle;

    internal static bool IsEnabled => handle?.EffectiveEnabled == true;

    internal static void Initialize()
    {
        if (handle != null)
        {
            return;
        }

        handle = OptionalNetworkFeatures.Get<MainFile>(FeatureId);
        handle.EffectiveEnabledChanged += HandleEffectiveEnabledChanged;
        ModLogger.Info(
            $"多人快速 SL：可选网络功能已注册，当前生效状态={handle.EffectiveEnabled}，请求状态={handle.RequestedEnabled}。");
    }

    private static void HandleEffectiveEnabledChanged(OptionalNetworkFeatureHandle feature)
    {
        MultiplayerQuickSlCoordinator.HandleFeatureStateChanged(feature.EffectiveEnabled);
        ModLogger.Info($"多人快速 SL：网络功能生效状态已切换为 {feature.EffectiveEnabled}。");
    }
}
