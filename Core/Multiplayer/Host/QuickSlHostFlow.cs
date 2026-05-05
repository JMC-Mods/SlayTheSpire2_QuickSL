using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using QuickSL.UI;
using System.Diagnostics.CodeAnalysis;

namespace QuickSL.Core;

internal sealed class QuickSlHostFlow(QuickSlMultiplayerController controller)
{
    private QuickSlMultiplayerState State => controller.State;

    private QuickSlMultiplayerContext Context => controller.Context;

    private QuickSlNetworkSender Sender => controller.Sender;

    private QuickSlRunSavePayloadService SavePayload => controller.SavePayload;

    private QuickSlMultiplayerReloader Reloader => controller.Reloader;

    public async Task RunHostAsync()
    {
        if (!TryPrepareHostQuickSl(out _, out INetHostGameService? hostService, out HashSet<ulong>? connectedPlayerIds))
        {
            return;
        }

        await RunApprovedHostQuickSlAsync(
            hostService,
            controller.CreateRequestId(),
            connectedPlayerIds,
            initiatingClientId: null);
    }

    public void HandleQuickSlInitiate(QuickSlInitiateMessage message, ulong senderId)
    {
        if (RunManager.Instance?.NetService is INetHostGameService currentHostService &&
            currentHostService.Type == NetGameType.Host &&
            !QuickSlSettings.AllowClientInitiatedQuickSl)
        {
            Sender.SendInitiateResponse(
                currentHostService,
                senderId,
                message.ClientRequestId,
                hostRequestId: 0,
                approved: false,
                QuickSlCancelReason.Disabled,
                waitingForOtherPlayers: false);
            ModLogger.Warn($"多人快速 SL：主机设置不允许客机发起，已拒绝玩家 {senderId} 的请求。");
            return;
        }

        if (!TryPrepareHostQuickSl(out _, out INetHostGameService? hostService, out HashSet<ulong>? connectedPlayerIds))
        {
            if (RunManager.Instance?.NetService is INetHostGameService fallbackHostService &&
                fallbackHostService.Type == NetGameType.Host)
            {
                Sender.SendInitiateResponse(
                    fallbackHostService,
                    senderId,
                    message.ClientRequestId,
                    hostRequestId: 0,
                    approved: false,
                    QuickSlCancelReason.InvalidState,
                    waitingForOtherPlayers: false);
            }

            return;
        }

        if (senderId == hostService.NetId || !connectedPlayerIds.Contains(senderId))
        {
            Sender.SendInitiateResponse(
                hostService,
                senderId,
                message.ClientRequestId,
                hostRequestId: 0,
                approved: false,
                QuickSlCancelReason.InvalidState,
                waitingForOtherPlayers: false);
            ModLogger.Warn($"多人快速 SL：收到非当前客机 {senderId} 的发起请求，已拒绝。");
            return;
        }

        uint hostRequestId = controller.CreateRequestId();
        var initiateState = new HostInitiateState(message.ClientRequestId, hostRequestId, senderId);
        State.HostInitiateState = initiateState;

        _ = RespondToClientInitiateAsync(hostService, connectedPlayerIds, initiateState);
    }

    public void HandleQuickSlVote(QuickSlVoteMessage message, ulong senderId)
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Host)
        {
            return;
        }

        HostVoteState? voteState = State.HostVoteState;
        if (voteState == null || voteState.RequestId != message.RequestId)
        {
            ModLogger.Warn($"多人快速 SL：收到过期或未知的确认回复，RequestId={message.RequestId}，Sender={senderId}。");
            return;
        }

        if (!voteState.ExpectedVotes.Contains(senderId))
        {
            ModLogger.Warn($"多人快速 SL：收到非预期玩家 {senderId} 的确认回复，已忽略。");
            return;
        }

        if (!message.Approved)
        {
            voteState.Cancel(QuickSlCancelReason.Rejected, senderId);
            ModLogger.Warn($"多人快速 SL：玩家 {senderId} 拒绝了本次 SL。");
            return;
        }

        voteState.ApprovedVotes.Add(senderId);
        ModLogger.Info($"多人快速 SL：玩家 {senderId} 已同意，进度 {voteState.ApprovedVotes.Count}/{voteState.ExpectedVotes.Count}。");

        if (voteState.ApprovedVotes.Count >= voteState.ExpectedVotes.Count)
        {
            voteState.Completion.TrySetResult(true);
        }
    }

    private async Task RespondToClientInitiateAsync(
        INetHostGameService hostService,
        HashSet<ulong> connectedPlayerIds,
        HostInitiateState initiateState)
    {
        bool approved = false;

        try
        {
            if (!QuickSlSettings.RequireMultiplayerHostConfirmation)
            {
                approved = true;
            }
            else
            {
                Sender.SendInitiatePending(
                    hostService,
                    initiateState.InitiatorPlayerId,
                    initiateState.ClientRequestId);
                approved = await QuickSlPopupService.ShowHostInitiateConfirmationAsync(
                    initiateState.InitiatorPlayerId,
                    hostService.Platform);
            }

            if (!ReferenceEquals(State.HostInitiateState, initiateState))
            {
                ModLogger.Info($"多人快速 SL：客机发起请求 {initiateState.ClientRequestId} 已失效，不再处理。");
                return;
            }

            if (!approved)
            {
                Sender.SendInitiateResponse(
                    hostService,
                    initiateState.InitiatorPlayerId,
                    initiateState.ClientRequestId,
                    initiateState.HostRequestId,
                    approved: false,
                    QuickSlCancelReason.Rejected,
                    waitingForOtherPlayers: false);
                ModLogger.Warn($"多人快速 SL：主机拒绝了客机 {initiateState.InitiatorPlayerId} 的发起请求。");
                return;
            }

            bool waitingForOtherPlayers =
                QuickSlSettings.RequireMultiplayerClientConfirmation &&
                connectedPlayerIds.Any(playerId =>
                    playerId != hostService.NetId &&
                    playerId != initiateState.InitiatorPlayerId);

            Sender.SendInitiateResponse(
                hostService,
                initiateState.InitiatorPlayerId,
                initiateState.ClientRequestId,
                initiateState.HostRequestId,
                approved: true,
                QuickSlCancelReason.InvalidState,
                waitingForOtherPlayers);
            ModLogger.Info($"多人快速 SL：主机已同意客机 {initiateState.InitiatorPlayerId} 发起，RequestId={initiateState.HostRequestId}。");

            await RunApprovedHostQuickSlAsync(
                hostService,
                initiateState.HostRequestId,
                connectedPlayerIds,
                initiateState.InitiatorPlayerId);
        }
        catch (Exception ex)
        {
            ModLogger.Error("多人快速 SL：处理客机发起请求失败。", ex);
            if (ReferenceEquals(State.HostInitiateState, initiateState))
            {
                Sender.SendInitiateResponse(
                    hostService,
                    initiateState.InitiatorPlayerId,
                    initiateState.ClientRequestId,
                    initiateState.HostRequestId,
                    approved: false,
                    QuickSlCancelReason.InvalidState,
                    waitingForOtherPlayers: false);
            }
        }
        finally
        {
            if (ReferenceEquals(State.HostInitiateState, initiateState))
            {
                State.HostInitiateState = null;
            }
        }
    }

    private bool TryPrepareHostQuickSl(
        [NotNullWhen(true)]
        out RunManager? runManager,
        [NotNullWhen(true)]
        out INetHostGameService? hostService,
        [NotNullWhen(true)]
        out HashSet<ulong>? connectedPlayerIds)
    {
        hostService = null;
        connectedPlayerIds = null;

        if (!Context.TryGetValidatedMultiplayerContext(
                requireHost: true,
                out _,
                out runManager,
                out INetGameService? netService))
        {
            return false;
        }

        if (Context.IsCombatStateSyncInProgress(runManager))
        {
            ModLogger.Warn("多人快速 SL 失败：当前多人状态同步尚未完成，请稍后再试。");
            return false;
        }

        if (netService is not INetHostGameService currentHostService)
        {
            ModLogger.Warn($"多人快速 SL 失败：当前主机网络服务类型异常，NetService={netService.GetType().Name}。");
            return false;
        }

        if (State.HostInitiateState != null)
        {
            ModLogger.Warn("多人快速 SL：已有客机发起请求正在等待主机处理。");
            return false;
        }

        if (State.HostVoteState != null)
        {
            ModLogger.Warn("多人快速 SL 已在等待客机确认，忽略重复触发。");
            return false;
        }

        if (State.HostLoadBarrierState != null)
        {
            ModLogger.Warn("多人快速 SL 已在等待客机载入准备，忽略重复触发。");
            return false;
        }

        hostService = currentHostService;
        connectedPlayerIds = Context.GetConnectedRunPlayerIds(runManager, netService);
        return true;
    }

    private async Task RunApprovedHostQuickSlAsync(
        INetHostGameService hostService,
        uint requestId,
        HashSet<ulong> connectedPlayerIds,
        ulong? initiatingClientId)
    {
        ulong[] remotePlayerIds = [.. connectedPlayerIds.Where(playerId => playerId != hostService.NetId)];

        if (remotePlayerIds.Length == 0)
        {
            SerializableRun? runSave = await SavePayload.LoadLocalMultiplayerRunSaveAsync(hostService);
            if (runSave == null)
            {
                return;
            }

            await Reloader.ExecuteLocalMultiplayerQuickSlAsync(requestId, connectedPlayerIds, runSave);
            return;
        }

        ulong[] votePlayerIds =
        [
            .. remotePlayerIds.Where(playerId => initiatingClientId == null || playerId != initiatingClientId.Value)
        ];

        if (votePlayerIds.Length > 0)
        {
            using var timeoutCancel = new CancellationTokenSource();
            var voteState = new HostVoteState(requestId, connectedPlayerIds, votePlayerIds, timeoutCancel);
            State.HostVoteState = voteState;

            try
            {
                var requestMessage = new QuickSlRequestMessage
                {
                    RequestId = requestId,
                    RequiresClientConfirmation = QuickSlSettings.RequireMultiplayerClientConfirmation,
                    InitiatorPlayerId = initiatingClientId ?? 0
                };

                foreach (ulong playerId in votePlayerIds)
                {
                    Sender.TrySendHostMessage(hostService, requestMessage, playerId);
                }

                if (QuickSlSettings.RequireMultiplayerClientConfirmation)
                {
                    QuickSlPopupService.ShowWaitingForPlayers(requestId);
                }

                _ = RunVoteTimeoutAsync(voteState);
                string requestMode = QuickSlSettings.RequireMultiplayerClientConfirmation ? "确认请求" : "静默准备请求";
                string initiatorNote = initiatingClientId == null ? string.Empty : "，已跳过发起客机";
                ModLogger.Info($"多人快速 SL：已向 {votePlayerIds.Length} 个客机发送{requestMode}{initiatorNote}，请等待所有人就绪。");

                bool approved = await voteState.Completion.Task;
                QuickSlPopupService.CloseWaiting(requestId);
                if (!approved)
                {
                    Sender.SendCancel(hostService, voteState);
                    _ = TaskHelper.RunSafely(voteState.CancelReason == QuickSlCancelReason.Rejected
                        ? QuickSlPopupService.ShowRejectedByPlayerAsync(
                            requestId,
                            voteState.RelatedPlayerId,
                            hostService.Platform)
                        : QuickSlPopupService.ShowQuickSlCanceledAsync(requestId));
                    ModLogger.Warn($"多人快速 SL 已取消，原因={voteState.CancelReason}。");
                    return;
                }
            }
            finally
            {
                QuickSlPopupService.CloseWaiting(requestId);
                timeoutCancel.Cancel();
                if (ReferenceEquals(State.HostVoteState, voteState))
                {
                    State.HostVoteState = null;
                }
            }
        }
        else if (initiatingClientId != null)
        {
            ModLogger.Info("多人快速 SL：除发起客机外没有其他客机需要确认。");
        }

        SerializableRun? approvedRunSave = await SavePayload.LoadLocalMultiplayerRunSaveAsync(hostService);
        if (approvedRunSave == null)
        {
            Sender.SendCancel(hostService, requestId, QuickSlCancelReason.InvalidState, remotePlayerIds);
            return;
        }

        string? runSaveJson = SavePayload.TrySerializeRunSavePayload(approvedRunSave);
        if (runSaveJson == null)
        {
            Sender.SendCancel(hostService, requestId, QuickSlCancelReason.InvalidState, remotePlayerIds);
            return;
        }

        await ExecuteApprovedHostReloadAsync(
            hostService,
            requestId,
            remotePlayerIds,
            connectedPlayerIds,
            approvedRunSave,
            runSaveJson);
    }

    private async Task RunVoteTimeoutAsync(HostVoteState voteState)
    {
        try
        {
            await Task.Delay(QuickSlMultiplayerController.VoteTimeout, voteState.TimeoutCancel.Token);
            if (ReferenceEquals(State.HostVoteState, voteState))
            {
                voteState.Cancel(QuickSlCancelReason.Timeout);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ModLogger.Error("多人快速 SL：等待客机确认超时时发生错误。", ex);
        }
    }

    private async Task ExecuteApprovedHostReloadAsync(
        INetHostGameService hostService,
        uint requestId,
        IReadOnlyCollection<ulong> remotePlayerIds,
        IReadOnlyCollection<ulong> connectedPlayerIds,
        SerializableRun runSave,
        string runSaveJson)
    {
        HostLoadBarrierState? loadBarrierState = remotePlayerIds.Count > 0
            ? new HostLoadBarrierState(requestId, remotePlayerIds)
            : null;

        if (loadBarrierState != null)
        {
            State.HostLoadBarrierState = loadBarrierState;
        }

        try
        {
            Sender.SendExecute(hostService, requestId, remotePlayerIds, connectedPlayerIds, runSaveJson);
            ModLogger.Info($"多人快速 SL：已通知 {remotePlayerIds.Count} 个客机同步执行。");
            await Reloader.ExecuteLocalMultiplayerQuickSlAsync(requestId, connectedPlayerIds, runSave);
        }
        finally
        {
            if (ReferenceEquals(State.HostLoadBarrierState, loadBarrierState))
            {
                State.HostLoadBarrierState = null;
            }
        }
    }
}
