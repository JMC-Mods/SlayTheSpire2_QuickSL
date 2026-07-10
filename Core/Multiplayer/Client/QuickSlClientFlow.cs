using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using QuickSL.UI;

namespace QuickSL.Core;

internal sealed class QuickSlClientFlow(QuickSlMultiplayerController controller)
{
    private QuickSlMultiplayerState State => controller.State;

    private QuickSlMultiplayerContext Context => controller.Context;

    private QuickSlNetworkSender Sender => controller.Sender;

    private QuickSlRunSavePayloadService SavePayload => controller.SavePayload;

    private QuickSlMultiplayerReloader Reloader => controller.Reloader;

    public Task RunClientAsync()
    {
        if (!Context.TryGetValidatedMultiplayerContext(requireHost: false, out _, out RunManager? runManager, out INetGameService? netService))
        {
            return Task.CompletedTask;
        }

        if (Context.IsCombatStateSyncInProgress(runManager))
        {
            ModLogger.Warn("多人快速 SL 失败：当前多人状态同步尚未完成，请稍后再试。");
            return Task.CompletedTask;
        }

        if (netService.Type != NetGameType.Client || netService is not INetClientGameService clientService)
        {
            ModLogger.Warn($"多人快速 SL 失败：当前不是客机模式，NetService={netService.Type}。");
            return Task.CompletedTask;
        }

        if (State.ClientInitiateState != null)
        {
            ModLogger.Warn("多人快速 SL：已向主机发起请求，正在等待主机处理。");
            return Task.CompletedTask;
        }

        if (State.ActiveClientRequestId.HasValue)
        {
            ModLogger.Warn("多人快速 SL：已有主机确认请求正在等待处理，暂不重复发起。");
            return Task.CompletedTask;
        }

        uint clientRequestId = controller.CreateRequestId();
        var initiateState = new ClientInitiateState(clientRequestId);
        State.ClientInitiateState = initiateState;

        if (!Sender.TrySendClientMessage(
                clientService,
                new QuickSlInitiateMessage
                {
                    ClientRequestId = clientRequestId
                }))
        {
            if (ReferenceEquals(State.ClientInitiateState, initiateState))
            {
                State.ClientInitiateState = null;
            }

            return Task.CompletedTask;
        }

        ModLogger.Info($"多人快速 SL：已向主机发起 SL 请求，ClientRequestId={clientRequestId}。");
        return Task.CompletedTask;
    }

    public void HandleQuickSlInitiatePending(QuickSlInitiatePendingMessage message, ulong senderId)
    {
        if (!Sender.IsExecuteSenderValid(senderId))
        {
            ModLogger.Warn($"多人快速 SL：收到非法主机等待确认消息，Sender={senderId}，ClientRequestId={message.ClientRequestId}。");
            return;
        }

        ClientInitiateState? initiateState = State.ClientInitiateState;
        if (initiateState == null || initiateState.ClientRequestId != message.ClientRequestId)
        {
            ModLogger.Warn($"多人快速 SL：收到过期或未知的主机等待确认消息，ClientRequestId={message.ClientRequestId}。");
            return;
        }

        QuickSlPopupService.ShowWaitingForHost(message.ClientRequestId);
        ModLogger.Info($"多人快速 SL：主机正在确认客机发起请求，ClientRequestId={message.ClientRequestId}。");
    }

    public void HandleQuickSlInitiateResponse(QuickSlInitiateResponseMessage message, ulong senderId)
    {
        if (!Sender.IsExecuteSenderValid(senderId))
        {
            ModLogger.Warn($"多人快速 SL：收到非法主机确认回复，Sender={senderId}，ClientRequestId={message.ClientRequestId}。");
            return;
        }

        ClientInitiateState? initiateState = State.ClientInitiateState;
        if (initiateState == null || initiateState.ClientRequestId != message.ClientRequestId)
        {
            ModLogger.Warn($"多人快速 SL：收到过期或未知的主机确认回复，ClientRequestId={message.ClientRequestId}。");
            return;
        }

        QuickSlPopupService.CloseWaiting(message.ClientRequestId);

        if (!message.Approved)
        {
            State.ClientInitiateState = null;
            _ = TaskHelper.RunSafely(message.Reason switch
            {
                QuickSlCancelReason.Disabled =>
                    QuickSlPopupService.ShowClientInitiationDisabledAsync(message.ClientRequestId),
                QuickSlCancelReason.Rejected =>
                    QuickSlPopupService.ShowHostRejectedClientInitiationAsync(message.ClientRequestId),
                _ => QuickSlPopupService.ShowQuickSlCanceledAsync(message.ClientRequestId)
            });
            ModLogger.Warn($"多人快速 SL：主机拒绝了客机发起请求，原因={message.Reason}。");
            return;
        }

        initiateState.HostRequestId = message.HostRequestId;
        if (message.WaitingForOtherPlayers)
        {
            QuickSlPopupService.ShowWaitingForPlayers(message.HostRequestId);
        }

        ModLogger.Info($"多人快速 SL：主机已同意客机发起请求，等待同步执行，RequestId={message.HostRequestId}。");
    }

    public void HandleQuickSlRequest(QuickSlRequestMessage message, ulong senderId)
    {
        if (!Context.TryGetValidatedMultiplayerContext(requireHost: false, out _, out RunManager? runManager, out INetGameService? netService))
        {
            if (Context.TryGetCurrentClientServiceForHost(senderId, out _))
            {
                Sender.SendClientVote(message.RequestId, approved: false);
                ModLogger.Warn($"多人快速 SL：当前状态无法响应主机请求，已拒绝 RequestId={message.RequestId}。");
            }

            return;
        }

        if (Context.IsCombatStateSyncInProgress(runManager))
        {
            ModLogger.Warn("多人快速 SL：当前多人状态同步尚未完成，已拒绝主机请求。");
            Sender.SendClientVote(message.RequestId, approved: false);
            return;
        }

        if (netService.Type != NetGameType.Client || netService is not INetClientGameService clientService)
        {
            ModLogger.Warn("多人快速 SL：非客机收到确认请求，已忽略。");
            return;
        }

        if (clientService.NetClient?.HostNetId != senderId)
        {
            ModLogger.Warn($"多人快速 SL：收到非主机 {senderId} 的确认请求，已忽略。");
            return;
        }

        if (State.ActiveClientRequestId.HasValue)
        {
            ModLogger.Warn("多人快速 SL：已有确认弹窗正在等待处理，拒绝新的请求。");
            Sender.SendClientVote(message.RequestId, approved: false);
            return;
        }

        State.ActiveClientRequestId = message.RequestId;
        _ = RespondToHostRequestAsync(message);
    }

    public void HandleQuickSlExecute(QuickSlExecuteMessage message, ulong senderId)
    {
        if (!Sender.IsExecuteSenderValid(senderId))
        {
            ModLogger.Warn($"多人快速 SL：收到非法执行消息，Sender={senderId}，RequestId={message.RequestId}。");
            return;
        }

        QuickSlPopupService.CloseWaiting(message.RequestId);
        State.ActiveClientRequestId = null;
        State.ClientInitiateState = null;
        SerializableRun? runSave = SavePayload.TryDeserializeRunSavePayload(message.RunSaveJson);
        if (runSave == null)
        {
            ModLogger.Warn($"多人快速 SL：主机执行消息缺少有效存档，RequestId={message.RequestId}。");
            return;
        }

        _ = Reloader.ExecuteLocalMultiplayerQuickSlAsync(message.RequestId, message.ConnectedPlayerIds, runSave);
    }

    public void HandleQuickSlCancel(QuickSlCancelMessage message, ulong senderId)
    {
        if (!Sender.IsExecuteSenderValid(senderId))
        {
            return;
        }

        if (State.ActiveClientRequestId == message.RequestId)
        {
            State.ActiveClientRequestId = null;
        }

        QuickSlPopupService.CloseWaiting(message.RequestId);

        if (State.ClientLoadBarrierState?.RequestId == message.RequestId)
        {
            State.ClientLoadBarrierState.Cancel();
            State.ClientLoadBarrierState = null;
        }

        if (State.ClientRunBeginBarrierState?.RequestId == message.RequestId)
        {
            State.ClientRunBeginBarrierState.Cancel();
            State.ClientRunBeginBarrierState = null;
        }

        if (State.ClientInitiateState?.HostRequestId == message.RequestId ||
            State.ClientInitiateState?.HostRequestId == null)
        {
            State.ClientInitiateState = null;
        }

        if (message.Reason == QuickSlCancelReason.Rejected)
        {
            _ = TaskHelper.RunSafely(QuickSlPopupService.ShowRejectedByPlayerAsync(
                message.RequestId,
                message.RelatedPlayerId,
                RunManager.Instance.NetService.Platform));
        }
        else
        {
            _ = TaskHelper.RunSafely(QuickSlPopupService.ShowQuickSlCanceledAsync(message.RequestId));
        }

        ModLogger.Warn($"多人快速 SL：主机取消了本次 SL，原因={message.Reason}。");
    }

    private async Task RespondToHostRequestAsync(QuickSlRequestMessage message)
    {
        bool approved = false;

        try
        {
            if (!message.RequiresClientConfirmation)
            {
                approved = true;
            }
            else
            {
                approved = await QuickSlPopupService.ShowClientConfirmationAsync(
                    message.InitiatorPlayerId,
                    RunManager.Instance.NetService.Platform);
            }

            if (State.ActiveClientRequestId != message.RequestId)
            {
                ModLogger.Info($"多人快速 SL：确认请求 {message.RequestId} 已失效，不再发送回复。");
                return;
            }

            Sender.SendClientVote(message.RequestId, approved);
            ModLogger.Info($"多人快速 SL：已向主机发送确认结果，Approved={approved}。");
        }
        catch (Exception ex)
        {
            ModLogger.Error("多人快速 SL：处理主机确认请求失败。", ex);
            if (State.ActiveClientRequestId == message.RequestId)
            {
                Sender.SendClientVote(message.RequestId, approved: false);
            }
        }
        finally
        {
            if (State.ActiveClientRequestId == message.RequestId)
            {
                State.ActiveClientRequestId = null;
            }
        }
    }
}
