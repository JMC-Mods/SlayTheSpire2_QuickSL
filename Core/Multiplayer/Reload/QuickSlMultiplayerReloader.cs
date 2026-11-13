using JmcModLib.Compat;
using JmcModLib.Reflection;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
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

    // 0.113 新增商店同步器；只按成员名称读取，避免旧版加载时绑定不存在的类型或属性。
    private static readonly Lazy<MemberAccessor?> MerchantSynchronizerAccessor = new(() =>
    {
        try
        {
            return MemberAccessor.Get(typeof(RunManager), "MerchantSynchronizer");
        }
        catch (MissingMemberException)
        {
            return null;
        }
    });

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
        bool useFastMode = QuickSlSettings.FastMode;
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
            IReadOnlyDictionary<ulong, QuickSlLobbyPlayerState> playerStateById =
                QuickSlLobbyCompat.CaptureRunLobbyPlayerStates(runManager.RunLobby);
            setupBarrierState = Barrier.PrepareHostSetupBarrier(requestId, originalNetService, connectedPlayerIds);

            SerializableRun? runSave = runSaveOverride == null
                ? await SavePayload.LoadLocalMultiplayerRunSaveAsync(originalNetService)
                : await SavePayload.PrepareRemoteRunSaveForLocalLoadAsync(runSaveOverride, originalNetService);
            if (runSave == null)
            {
                return;
            }

            RunState runState = RunState.FromSerializable(runSave);
            // 代理生成只依赖运行时接口，应在取消队列和拆除旧同步器之前完成。
            // 即使未来遇到未知接口形态，也能在不破坏当前联机局的情况下中止。
            INetGameService protectedNetService =
                DisconnectSuppressingNetGameService.Create(originalNetService);
            object loadLobbyListener = PassiveLoadRunLobbyListener.CreateListener();
            // 在拆除旧局前读取新增同步器；未知成员形态应中止本次 SL，不能留下旧订阅继续重载。
            IDisposable? merchantSynchronizer = GetMerchantSynchronizer(runManager);

            ModLogger.Info($"多人快速 SL：执行同步重载，RequestId={requestId}，在线玩家数={connectedPlayerIds.Count}。");
            QuickSlAsyncOperationGuard.CancelPendingGameWaits();
            runManager.ActionExecutor.Cancel();
            runManager.ActionQueueSet.Reset();
            NRunMusicController.Instance?.StopMusic();

            fadedOut = await QuickSlTransitionGuard.FadeOutAsync(game.Transition, useFastMode);

            using IDisposable stableTopBarLocation = QuickSlSceneReloadGuard.PreserveStableTopBarLocation();
            QuickSlSceneReloadGuard.PrepareCurrentHandForSceneSwap();
            DisposeNetworkPreservedRunSystems(runManager);

            NetServiceAccessor.SetValue(runManager, protectedNetService);
            try
            {
                QuickSlRunManagerCompat.CleanUpForQuickSlReload(runManager);
                cleanedUp = true;
                if (merchantSynchronizer != null)
                {
                    // 原版清理会断开连接，但不会释放商店订阅；保留连接时必须显式释放。
                    // 失败交给外层恢复到主菜单，避免继续复用残留商店消息处理器的连接。
                    try
                    {
                        merchantSynchronizer.Dispose();
                        ModLogger.Debug("多人快速 SL：已清理旧局网络同步器 MerchantSynchronizer。");
                    }
                    catch
                    {
                        originalNetService.Disconnect(NetError.InternalError, now: true);
                        throw;
                    }
                }
            }
            finally
            {
                NetServiceAccessor.SetValue(runManager, originalNetService);
            }

            loadLobby = QuickSlLobbyCompat.CreateLoadRunLobby(
                originalNetService,
                runSave,
                loadLobbyListener);
            QuickSlLobbyCompat.AddConnectedPlayersToLoadLobby(
                loadLobby,
                originalNetService,
                connectedPlayerIds,
                playerStateById);
            game.RemoteCursorContainer.Initialize(
                loadLobby.InputSynchronizer,
                MultiplayerCompat.GetLoadRunLobbyPlayerIds(loadLobby));
            game.ReactionContainer.InitializeNetworking(loadLobby.NetService);

            await Barrier.WaitForCoordinatedLoadBeginAsync(requestId, originalNetService, connectedPlayerIds);

            await QuickSlRunManagerCompat.SetUpSavedMultiPlayerAsync(runManager, runState, loadLobby);
            QuickSlLobbyCompat.KeepOnlyConnectedRunLobbyPlayers(runManager, connectedPlayerIds);
            controller.EnsureHandlersRegistered();

            await Barrier.WaitForCoordinatedRunBeginAsync(requestId, originalNetService, connectedPlayerIds);

            using (QuickSlSceneReloadGuard.SuppressLateHandLayoutRefresh())
            using (QuickSlTransitionGuard.SuppressTransitions(useFastMode))
            {
                await game.LoadRun(runState, runSave.PreFinishedRoom);
            }

            QuickSlRunManagerCompat.CleanUpLoadRunLobby(loadLobby, disconnectSession: false);
            loadLobby = null;

            await QuickSlTransitionGuard.FadeInAsync(game.Transition, useFastMode);
            fadedOut = false;

            ModLogger.Info($"多人快速 SL 完成，RequestId={requestId}。");
        }
        catch (Exception ex)
        {
            ModLogger.Error("多人快速 SL 执行失败。", ex);
            if (loadLobby != null)
            {
                QuickSlRunManagerCompat.CleanUpLoadRunLobby(loadLobby, disconnectSession: false);
            }

            await TryRecoverAsync(fadedOut, cleanedUp, useFastMode);
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

    private static IDisposable? GetMerchantSynchronizer(RunManager runManager)
    {
        return MerchantSynchronizerAccessor.Value?.GetValue(runManager) switch
        {
            null => null,
            IDisposable synchronizer => synchronizer,
            _ => throw new InvalidOperationException("当前游戏版本的商店同步器无法释放，已中止多人快速 SL。")
        };
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
            ModLogger.Error("多人快速 SL 失败后恢复界面时再次出错。", ex);
        }
    }
}
