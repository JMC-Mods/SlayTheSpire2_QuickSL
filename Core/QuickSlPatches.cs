using Godot;
using HarmonyLib;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Nodes;
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

[HarmonyPatch(typeof(NTransition), nameof(NTransition.FadeOut))]
internal static class NTransitionFadeOutPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ref Task __result)
    {
        return QuickSlTransitionGuard.TrySkipTransition("屏幕淡出", ref __result);
    }
}

[HarmonyPatch(typeof(NTransition), nameof(NTransition.FadeIn))]
internal static class NTransitionFadeInPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ref Task __result)
    {
        return QuickSlTransitionGuard.TrySkipTransition("屏幕淡入", ref __result);
    }
}

[HarmonyPatch(typeof(NTransition), nameof(NTransition.RoomFadeOut))]
internal static class NTransitionRoomFadeOutPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ref Task __result)
    {
        return QuickSlTransitionGuard.TrySkipTransition("房间淡出", ref __result);
    }
}

[HarmonyPatch(typeof(NTransition), nameof(NTransition.RoomFadeIn))]
internal static class NTransitionRoomFadeInPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ref Task __result)
    {
        return QuickSlTransitionGuard.TrySkipTransition("房间淡入", ref __result);
    }
}

internal static class QuickSlTransitionGuard
{
    private const float InstantTransitionSeconds = 0f;

    private static int suppressTransitionDepth;

    public static async Task<bool> FadeOutAsync(NTransition transition, bool useFastMode, bool useInstantCover)
    {
        if (!useFastMode)
        {
            await transition.FadeOut();
            return true;
        }

        if (!useInstantCover)
        {
            ModLogger.Debug("快速 SL：快速模式使用旧版裸跳过转场，重载过程可能短暂显示未稳定的界面。");
            return false;
        }

        ModLogger.Debug("快速 SL：快速模式使用瞬时遮罩隐藏重载过程。");
        await transition.FadeOut(InstantTransitionSeconds);
        return true;
    }

    public static Task FadeInAsync(NTransition transition, bool useFastMode, bool useInstantCover)
    {
        if (!useFastMode)
        {
            return transition.FadeIn();
        }

        return useInstantCover
            ? transition.FadeIn(InstantTransitionSeconds)
            : Task.CompletedTask;
    }

    public static IDisposable SuppressTransitions(bool shouldSuppress)
    {
        if (!shouldSuppress)
        {
            return NoopSuppression.Instance;
        }

        Interlocked.Increment(ref suppressTransitionDepth);
        return new TransitionSuppression();
    }

    public static bool TrySkipTransition(string transitionName, ref Task result)
    {
        if (Volatile.Read(ref suppressTransitionDepth) <= 0)
        {
            return true;
        }

        ModLogger.Trace($"快速 SL：快速模式跳过{transitionName}动画。");
        result = Task.CompletedTask;
        return false;
    }

    private sealed class TransitionSuppression : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Interlocked.Decrement(ref suppressTransitionDepth);
        }
    }

    private sealed class NoopSuppression : IDisposable
    {
        public static readonly NoopSuppression Instance = new();

        private NoopSuppression()
        {
        }

        public void Dispose()
        {
        }
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
