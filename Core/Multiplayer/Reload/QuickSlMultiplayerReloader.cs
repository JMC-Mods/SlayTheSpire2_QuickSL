using JmcModLib.Reflection;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace QuickSL.Core;

internal sealed class QuickSlMultiplayerReloader(QuickSlMultiplayerController controller)
{
    private static readonly MemberAccessor NetServiceAccessor =
        MemberAccessor.Get(typeof(RunManager), nameof(RunManager.NetService));

    private static readonly MemberAccessor RunLobbyConnectedPlayerIdsAccessor =
        MemberAccessor.Get(typeof(RunLobby), "_connectedPlayerIds");

    private readonly SemaphoreSlim reloadLock = new(1, 1);

    private QuickSlMultiplayerState State => controller.State;

    private QuickSlMultiplayerContext Context => controller.Context;

    private QuickSlRunSavePayloadService SavePayload => controller.SavePayload;

    private QuickSlLoadBarrierCoordinator Barrier => controller.Barrier;

    public async Task ExecuteLocalMultiplayerQuickSlAsync(
        uint requestId,
        IReadOnlyCollection<ulong>? connectedPlayerIdsOverride = null,
        SerializableRun? runSaveOverride = null)
    {
        if (!await reloadLock.WaitAsync(TimeSpan.Zero))
        {
            ModLogger.Warn("多人快速 SL 已在执行中，忽略重复执行消息。");
            return;
        }

        bool fadedOut = false;
        bool cleanedUp = false;
        LoadRunLobby? loadLobby = null;
        HostLoadBarrierState? setupBarrierState = null;

        try
        {
            if (!Context.TryGetValidatedMultiplayerContext(requireHost: false, out NGame? game, out RunManager? runManager, out INetGameService? originalNetService))
            {
                return;
            }

            HashSet<ulong> connectedPlayerIds = connectedPlayerIdsOverride == null
                ? Context.GetConnectedRunPlayerIds(runManager, originalNetService)
                : [.. connectedPlayerIdsOverride];
            connectedPlayerIds.Add(originalNetService.NetId);
            setupBarrierState = Barrier.PrepareHostSetupBarrier(requestId, originalNetService, connectedPlayerIds);

            SerializableRun? runSave = runSaveOverride == null
                ? await SavePayload.LoadLocalMultiplayerRunSaveAsync(originalNetService)
                : await SavePayload.PrepareRemoteRunSaveForLocalLoadAsync(runSaveOverride, originalNetService);
            if (runSave == null)
            {
                return;
            }

            RunState runState = RunState.FromSerializable(runSave);

            ModLogger.Info($"多人快速 SL：执行同步重载，RequestId={requestId}，在线玩家数={connectedPlayerIds.Count}。");
            runManager.ActionQueueSet.Reset();
            NRunMusicController.Instance?.StopMusic();

            await game.Transition.FadeOut();
            fadedOut = true;

            QuickSlSceneReloadGuard.PrepareCurrentHandForSceneSwap();
            DisposeNetworkPreservedRunSystems(runManager);

            var protectedNetService = new DisconnectSuppressingNetGameService(originalNetService);
            NetServiceAccessor.SetValue(runManager, protectedNetService);
            try
            {
                runManager.CleanUp();
                cleanedUp = true;
            }
            finally
            {
                NetServiceAccessor.SetValue(runManager, originalNetService);
            }

            loadLobby = new LoadRunLobby(originalNetService, PassiveLoadRunLobbyListener.Instance, runSave);
            AddConnectedPlayersToLoadLobby(loadLobby, originalNetService, connectedPlayerIds);
            game.RemoteCursorContainer.Initialize(loadLobby.InputSynchronizer, loadLobby.ConnectedPlayerIds);
            game.ReactionContainer.InitializeNetworking(loadLobby.NetService);

            await Barrier.WaitForCoordinatedLoadBeginAsync(requestId, originalNetService, connectedPlayerIds);

            await QuickSlRunManagerCompat.SetUpSavedMultiPlayerAsync(runManager, runState, loadLobby);
            KeepOnlyConnectedPlayersInRunLobby(runManager, loadLobby.ConnectedPlayerIds);
            controller.EnsureHandlersRegistered();

            await Barrier.WaitForCoordinatedRunBeginAsync(requestId, originalNetService, connectedPlayerIds);

            using (QuickSlSceneReloadGuard.SuppressLateHandLayoutRefresh())
            {
                await game.LoadRun(runState, runSave.PreFinishedRoom);
            }

            loadLobby.CleanUp(disconnectSession: false);
            loadLobby = null;

            await game.Transition.FadeIn();
            ModLogger.Info($"多人快速 SL 完成，RequestId={requestId}。");
        }
        catch (Exception ex)
        {
            ModLogger.Error("多人快速 SL 执行失败。", ex);
            loadLobby?.CleanUp(disconnectSession: false);
            await TryRecoverAsync(fadedOut, cleanedUp);
        }
        finally
        {
            if (ReferenceEquals(State.HostSetupBarrierState, setupBarrierState))
            {
                State.HostSetupBarrierState = null;
            }

            reloadLock.Release();
        }
    }

    private static void AddConnectedPlayersToLoadLobby(
        LoadRunLobby loadLobby,
        INetGameService netService,
        IEnumerable<ulong> connectedPlayerIds)
    {
        if (netService.Type == NetGameType.Host)
        {
            loadLobby.AddLocalHostPlayer();
        }

        foreach (ulong playerId in connectedPlayerIds)
        {
            loadLobby.ConnectedPlayerIds.Add(playerId);
        }
    }

    private static void KeepOnlyConnectedPlayersInRunLobby(RunManager runManager, IReadOnlySet<ulong> connectedPlayerIds)
    {
        if (runManager.RunLobby == null)
        {
            ModLogger.Warn("多人快速 SL：RunLobby 尚未初始化，无法修正已连接玩家列表。");
            return;
        }

        if (RunLobbyConnectedPlayerIdsAccessor.GetValue(runManager.RunLobby) is not HashSet<ulong> runLobbyConnectedPlayerIds)
        {
            ModLogger.Warn("多人快速 SL：读取 RunLobby 已连接玩家列表失败。");
            return;
        }

        ulong[] disconnectedPlayerIds =
        [
            .. runLobbyConnectedPlayerIds
                .Where(playerId => !connectedPlayerIds.Contains(playerId))
        ];

        foreach (ulong playerId in disconnectedPlayerIds)
        {
            runLobbyConnectedPlayerIds.Remove(playerId);
            runManager.InputSynchronizer.OnPlayerDisconnected(playerId);
        }

        if (disconnectedPlayerIds.Length > 0)
        {
            ModLogger.Info($"多人快速 SL：已将 {disconnectedPlayerIds.Length} 个未连接玩家从本次加载同步等待中移除。");
        }
    }

    private static void DisposeNetworkPreservedRunSystems(RunManager runManager)
    {
        TryDisposeRunSystem("CombatStateSynchronizer", runManager.CombatStateSynchronizer);
        TryDisposeRunSystem("EventSynchronizer", runManager.EventSynchronizer);
        TryDisposeRunSystem("OneOffSynchronizer", runManager.OneOffSynchronizer);
        TryDisposeRunSystem("InputSynchronizer", runManager.InputSynchronizer);
    }

    private static void TryDisposeRunSystem(string name, IDisposable? disposable)
    {
        if (disposable == null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
            ModLogger.Debug($"多人快速 SL：已清理旧局网络同步器 {name}。");
        }
        catch (Exception ex)
        {
            ModLogger.Warn($"多人快速 SL：清理旧局网络同步器 {name} 时出现异常：{ex}");
        }
    }

    private static async Task TryRecoverAsync(bool fadedOut, bool cleanedUp)
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
                await game.Transition.FadeIn();
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error("多人快速 SL 失败后恢复界面时再次出错。", ex);
        }
    }
}
