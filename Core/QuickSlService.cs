using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using QuickSL.UI;

namespace QuickSL.Core;

public static class QuickSlService
{
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    public static void RequestQuickSl()
    {
        _ = RequestQuickSlAsync();
    }

    public static async Task RequestQuickSlAsync()
    {
        if (!RunLock.Wait(TimeSpan.Zero))
        {
            ModLogger.Warn("快速 SL 已在执行中，忽略重复触发。");
            return;
        }

        await RunQuickSlAsync();
    }

    private static async Task RunQuickSlAsync()
    {
        try
        {
            NetGameType netType = RunManager.Instance.NetService.Type;
            if (netType == NetGameType.Host)
            {
                if (!QuickSlMultiplayerFeature.IsEnabled)
                {
                    ModLogger.Warn("多人快速 SL 未启用，本次操作不会回退到单人 SL。");
                    await QuickSlPopupService.ShowMultiplayerFeatureDisabledAsync();
                    return;
                }

                await MultiplayerQuickSlCoordinator.RunHostAsync();
                return;
            }

            if (netType == NetGameType.Client)
            {
                if (!QuickSlMultiplayerFeature.IsEnabled)
                {
                    ModLogger.Warn("多人快速 SL 未启用，本次操作不会回退到单人 SL。");
                    await QuickSlPopupService.ShowMultiplayerFeatureDisabledAsync();
                    return;
                }

                await MultiplayerQuickSlCoordinator.RunClientAsync();
                return;
            }

            await RunSinglePlayerQuickSlAsync();
        }
        catch (Exception ex)
        {
            ModLogger.Error("快速 SL 分流执行失败。", ex);
        }
        finally
        {
            RunLock.Release();
        }
    }

    private static async Task RunSinglePlayerQuickSlAsync()
    {
        bool fadedOut = false;
        bool cleanedUp = false;
        bool useFastMode = QuickSlSettings.FastMode;

        try
        {
            NGame? game = NGame.Instance;
            if (game == null)
            {
                ModLogger.Warn("快速 SL 失败：NGame 尚未初始化。");
                return;
            }

            if (game.Transition.InTransition)
            {
                ModLogger.Warn("快速 SL 失败：当前正在切换场景。");
                return;
            }

            RunManager runManager = RunManager.Instance;
            if (!runManager.IsInProgress)
            {
                ModLogger.Warn("快速 SL 失败：当前不在一局游戏中。");
                return;
            }

            if (runManager.IsCleaningUp)
            {
                ModLogger.Warn("快速 SL 失败：当前局正在清理中。");
                return;
            }

            if (runManager.NetService.Type != NetGameType.Singleplayer)
            {
                ModLogger.Warn($"快速 SL 失败：暂不支持 {runManager.NetService.Type} 模式。");
                return;
            }

            SaveManager saveManager = SaveManager.Instance;
            Task? currentSaveTask = saveManager.CurrentRunSaveTask;
            if (currentSaveTask != null)
            {
                ModLogger.Info("快速 SL：等待当前存档任务完成。");
                await currentSaveTask;
            }

            if (!saveManager.HasRunSave)
            {
                ModLogger.Warn("快速 SL 失败：没有找到当前局存档。");
                return;
            }

            ReadSaveResult<SerializableRun> readResult = saveManager.LoadRunSave();
            if (!readResult.Success || readResult.SaveData == null)
            {
                ModLogger.Warn($"快速 SL 失败：读取当前局存档失败，状态={readResult.Status}。");
                return;
            }

            SerializableRun runSave = readResult.SaveData;
            RunState runState = RunState.FromSerializable(runSave);

            ModLogger.Info($"快速 SL：重新载入当前局，角色={runSave.Players[0].CharacterId}。");
            QuickSlAsyncOperationGuard.CancelPendingGameWaits();
            runManager.ActionExecutor.Cancel();
            runManager.ActionQueueSet.Reset();
            NRunMusicController.Instance?.StopMusic();

            fadedOut = await QuickSlTransitionGuard.FadeOutAsync(game.Transition, useFastMode);

            using IDisposable stableTopBarLocation = QuickSlSceneReloadGuard.PreserveStableTopBarLocation();
            QuickSlSceneReloadGuard.PrepareCurrentHandForSceneSwap();
            QuickSlRunManagerCompat.CleanUpForQuickSlReload(runManager);
            cleanedUp = true;

            await QuickSlRunManagerCompat.SetUpSavedSinglePlayerAsync(runManager, runState, runSave);
            game.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());
            using (QuickSlSceneReloadGuard.SuppressLateHandLayoutRefresh())
            using (QuickSlTransitionGuard.SuppressTransitions(useFastMode))
            {
                await game.LoadRun(runState, runSave.PreFinishedRoom);
            }

            await QuickSlTransitionGuard.FadeInAsync(game.Transition, useFastMode);
            fadedOut = false;

            ModLogger.Info("快速 SL 完成。");
        }
        catch (Exception ex)
        {
            ModLogger.Error("快速 SL 执行失败。", ex);
            await TryRecoverAsync(fadedOut, cleanedUp, useFastMode);
        }
    }

    private static async Task TryRecoverAsync(bool fadedOut, bool cleanedUp, bool useFastMode)
    {
        try
        {
            if (NGame.Instance is not { } game)
            {
                return;
            }

            if (cleanedUp)
            {
                await game.ReturnToMainMenu();
                return;
            }

            if (fadedOut)
            {
                await QuickSlTransitionGuard.FadeInAsync(game.Transition, useFastMode);
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error("快速 SL 失败后恢复界面时再次出错。", ex);
        }
    }
}
