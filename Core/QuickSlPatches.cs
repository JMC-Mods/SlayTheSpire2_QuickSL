using Godot;
using HarmonyLib;
using JmcModLib.Reflection;
using JmcModLib.Utils;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.sts2.Core.Nodes.TopBar;

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

[HarmonyPatch(typeof(NTopBar), nameof(NTopBar.Initialize))]
internal static class NTopBarInitializePatch
{
    [HarmonyPostfix]
    private static void Postfix(NTopBar __instance, IRunState runState)
    {
        QuickSlSceneReloadGuard.RestoreStableTopBarLocationIfNeeded(__instance, runState);
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
    private static readonly object TopBarLocationSnapshotLock = new();
    private static readonly Lazy<TopBarLocationAccessors?> TopBarLocationAccessorsValue =
        new(TryCreateTopBarLocationAccessors);

    private static int suppressLateHandLayoutRefreshDepth;
    private static int preserveTopBarLocationDepth;
    private static TopBarLocationSnapshot? topBarLocationSnapshot;

    public static IDisposable SuppressLateHandLayoutRefresh()
    {
        Interlocked.Increment(ref suppressLateHandLayoutRefreshDepth);
        return new LateHandLayoutRefreshSuppression();
    }

    private static TopBarLocationAccessors? TopBarLocationAccessorsOrNull => TopBarLocationAccessorsValue.Value;

    public static IDisposable PreserveStableTopBarLocation()
    {
        TopBarLocationSnapshot? snapshot = TopBarLocationSnapshot.Capture();
        if (snapshot == null)
        {
            return NoopSuppression.Instance;
        }

        lock (TopBarLocationSnapshotLock)
        {
            topBarLocationSnapshot = snapshot;
            preserveTopBarLocationDepth++;
        }

        ModLogger.Trace("快速 SL：已缓存旧 TopBar 的层数与房间图标显示。");
        return new TopBarLocationPreservation();
    }

    public static void RestoreStableTopBarLocationIfNeeded(NTopBar topBar, IRunState runState)
    {
        if (runState.CurrentRoom != null)
        {
            return;
        }

        TopBarLocationSnapshot? snapshot;
        lock (TopBarLocationSnapshotLock)
        {
            if (preserveTopBarLocationDepth <= 0)
            {
                return;
            }

            snapshot = topBarLocationSnapshot;
        }

        if (snapshot == null)
        {
            return;
        }

        try
        {
            snapshot.Apply(topBar, runState);
            ModLogger.Trace("快速 SL：新 TopBar 初始化时已沿用旧层数与房间图标显示。");
        }
        catch (Exception ex)
        {
            ModLogger.Warn("快速 SL：恢复 TopBar 层数与房间图标显示失败，将继续执行 SL。", ex);
        }
    }

    private static TopBarLocationAccessors? TryCreateTopBarLocationAccessors()
    {
        try
        {
            return new TopBarLocationAccessors(
                MemberAccessor.Get(typeof(NTopBarFloorIcon), "_floorNumLabel"),
                MemberAccessor.Get(typeof(NTopBarRoomIcon), "_roomIcon"),
                MemberAccessor.Get(typeof(NTopBarRoomIcon), "_roomIconOutline"));
        }
        catch (Exception ex)
        {
            ModLogger.Warn($"快速 SL：当前游戏版本的 TopBar 内部字段已变化，将禁用层数与房间图标快照修复：{ex.Message}");
            return null;
        }
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

    private sealed class TopBarLocationPreservation : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            lock (TopBarLocationSnapshotLock)
            {
                preserveTopBarLocationDepth--;
                if (preserveTopBarLocationDepth <= 0)
                {
                    preserveTopBarLocationDepth = 0;
                    topBarLocationSnapshot = null;
                }
            }
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

    private sealed record TopBarLocationAccessors(
        MemberAccessor FloorNumLabel,
        MemberAccessor RoomIcon,
        MemberAccessor RoomIconOutline);

    private sealed record TopBarLocationSnapshot(
        string? FloorText,
        Texture2D? RoomIconTexture,
        bool RoomIconVisible,
        Texture2D? RoomIconOutlineTexture,
        bool RoomIconOutlineVisible,
        Control.FocusModeEnum RoomIconFocusMode,
        Control.MouseFilterEnum RoomIconMouseFilter)
    {
        public static TopBarLocationSnapshot? Capture()
        {
            try
            {
                TopBarLocationAccessors? accessors = TopBarLocationAccessorsOrNull;
                if (accessors == null)
                {
                    return null;
                }

                NTopBar? topBar = NRun.Instance?.GlobalUi?.TopBar;
                if (topBar == null || !GodotObject.IsInstanceValid(topBar))
                {
                    return null;
                }

                MegaLabel? floorLabel = GetFloorLabel(accessors, topBar.FloorIcon);
                TextureRect? roomIcon = GetRoomIcon(accessors, topBar.RoomIcon);
                TextureRect? roomIconOutline = GetRoomIconOutline(accessors, topBar.RoomIcon);
                if (floorLabel == null && roomIcon == null && roomIconOutline == null)
                {
                    return null;
                }

                return new TopBarLocationSnapshot(
                    floorLabel?.Text,
                    roomIcon?.Texture,
                    roomIcon?.Visible ?? false,
                    roomIconOutline?.Texture,
                    roomIconOutline?.Visible ?? false,
                    topBar.RoomIcon.FocusMode,
                    topBar.RoomIcon.MouseFilter);
            }
            catch (Exception ex)
            {
                ModLogger.Warn("快速 SL：读取旧 TopBar 层数与房间图标失败，将跳过本次快照修复。", ex);
                return null;
            }
        }

        public void Apply(NTopBar topBar, IRunState runState)
        {
            TopBarLocationAccessors? accessors = TopBarLocationAccessorsOrNull;
            if (accessors == null)
            {
                return;
            }

            if (!GodotObject.IsInstanceValid(topBar))
            {
                return;
            }

            MegaLabel? floorLabel = GetFloorLabel(accessors, topBar.FloorIcon);
            if (floorLabel != null)
            {
                string floorText = !string.IsNullOrWhiteSpace(FloorText)
                    ? FloorText
                    : runState.TotalFloor.ToString();
                floorLabel.SetTextAutoSize(floorText);
            }

            TextureRect? roomIcon = GetRoomIcon(accessors, topBar.RoomIcon);
            if (roomIcon != null)
            {
                roomIcon.Texture = RoomIconTexture;
                roomIcon.Visible = RoomIconVisible;
            }

            TextureRect? roomIconOutline = GetRoomIconOutline(accessors, topBar.RoomIcon);
            if (roomIconOutline != null)
            {
                roomIconOutline.Texture = RoomIconOutlineTexture;
                roomIconOutline.Visible = RoomIconOutlineVisible;
            }

            topBar.RoomIcon.FocusMode = RoomIconFocusMode;
            topBar.RoomIcon.MouseFilter = RoomIconMouseFilter;
        }

        private static MegaLabel? GetFloorLabel(TopBarLocationAccessors accessors, NTopBarFloorIcon floorIcon)
        {
            return GodotObject.IsInstanceValid(floorIcon)
                ? accessors.FloorNumLabel.GetValue(floorIcon) as MegaLabel
                : null;
        }

        private static TextureRect? GetRoomIcon(TopBarLocationAccessors accessors, NTopBarRoomIcon roomIcon)
        {
            return GodotObject.IsInstanceValid(roomIcon)
                ? accessors.RoomIcon.GetValue(roomIcon) as TextureRect
                : null;
        }

        private static TextureRect? GetRoomIconOutline(TopBarLocationAccessors accessors, NTopBarRoomIcon roomIcon)
        {
            return GodotObject.IsInstanceValid(roomIcon)
                ? accessors.RoomIconOutline.GetValue(roomIcon) as TextureRect
                : null;
        }
    }
}
