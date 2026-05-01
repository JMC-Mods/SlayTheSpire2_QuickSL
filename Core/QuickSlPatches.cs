using HarmonyLib;
using Godot;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;

namespace QuickSL.Core;

[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
internal static class RunManagerLaunchPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        MultiplayerQuickSlCoordinator.EnsureHandlersRegistered();
    }
}

[HarmonyPatch(typeof(NPlayerHand), "OnHolderUnfocused")]
internal static class NPlayerHandOnHolderUnfocusedPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NPlayerHand __instance, NHandCardHolder holder)
    {
        if (!QuickSlSceneReloadGuard.ShouldSkipLateHandUnfocus(__instance, holder))
        {
            return true;
        }

        ModLogger.Debug("快速 SL：旧手牌已离开场景树，跳过失焦布局刷新。");
        return false;
    }
}

internal static class QuickSlSceneReloadGuard
{
    private static int suppressLateHandLayoutRefreshDepth;

    public static IDisposable SuppressLateHandLayoutRefresh()
    {
        Interlocked.Increment(ref suppressLateHandLayoutRefreshDepth);
        return new LateHandLayoutRefreshSuppression();
    }

    public static bool ShouldSkipLateHandUnfocus(NPlayerHand hand, NHandCardHolder? holder)
    {
        if (Volatile.Read(ref suppressLateHandLayoutRefreshDepth) <= 0)
        {
            return false;
        }

        if (!GodotObject.IsInstanceValid(hand) || !hand.IsInsideTree())
        {
            return true;
        }

        if (hand.CardHolderContainer is not { } container ||
            !GodotObject.IsInstanceValid(container) ||
            !container.IsInsideTree())
        {
            return true;
        }

        return holder == null || !GodotObject.IsInstanceValid(holder) || !holder.IsInsideTree();
    }

    public static void PrepareCurrentHandForSceneSwap()
    {
        try
        {
            if (NPlayerHand.Instance is not { } hand ||
                !GodotObject.IsInstanceValid(hand) ||
                !hand.IsInsideTree())
            {
                return;
            }

            int preparedCount = 0;
            foreach (NHandCardHolder holder in hand.ActiveHolders)
            {
                if (!GodotObject.IsInstanceValid(holder) || !holder.IsInsideTree())
                {
                    continue;
                }

                holder.ReleaseFocus();
                NClickableControl hitbox = holder.Hitbox;
                if (!GodotObject.IsInstanceValid(hitbox))
                {
                    continue;
                }

                hitbox.ReleaseFocus();
                if (hitbox.IsEnabled)
                {
                    hitbox.SetEnabled(false);
                    preparedCount++;
                }
            }

            if (preparedCount > 0)
            {
                ModLogger.Debug($"快速 SL：切换场景前已释放 {preparedCount} 张旧手牌的焦点。");
            }
        }
        catch (Exception ex)
        {
            ModLogger.Warn("快速 SL：预处理旧手牌焦点失败，将继续执行 SL。", ex);
        }
    }

    private sealed class LateHandLayoutRefreshSuppression : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Interlocked.Decrement(ref suppressLateHandLayoutRefreshDepth);
        }
    }
}
