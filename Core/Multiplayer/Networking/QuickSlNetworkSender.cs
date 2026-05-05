using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;

namespace QuickSL.Core;

internal sealed class QuickSlNetworkSender
{
    public void SendClientVote(uint requestId, bool approved)
    {
        INetGameService netService = RunManager.Instance.NetService;
        if (!netService.IsConnected)
        {
            ModLogger.Warn($"多人快速 SL：当前网络服务已断开，无法发送确认结果 RequestId={requestId}。");
            return;
        }

        netService.SendMessage(new QuickSlVoteMessage
        {
            RequestId = requestId,
            Approved = approved
        });
    }

    public void SendExecute(
        INetHostGameService hostService,
        uint requestId,
        IEnumerable<ulong> remotePlayerIds,
        IReadOnlyCollection<ulong> connectedPlayerIds,
        string runSaveJson)
    {
        var executeMessage = new QuickSlExecuteMessage
        {
            RequestId = requestId,
            ConnectedPlayerIds = [.. connectedPlayerIds],
            RunSaveJson = runSaveJson
        };

        foreach (ulong playerId in remotePlayerIds)
        {
            TrySendHostMessage(hostService, executeMessage, playerId);
        }
    }

    public void SendCancel(INetHostGameService hostService, HostVoteState voteState)
    {
        SendCancel(
            hostService,
            voteState.RequestId,
            voteState.CancelReason,
            voteState.ConnectedPlayerIds.Where(playerId => playerId != hostService.NetId),
            voteState.RelatedPlayerId);
    }

    public void SendCancel(
        INetHostGameService hostService,
        uint requestId,
        QuickSlCancelReason reason,
        IEnumerable<ulong> playerIds,
        ulong relatedPlayerId = 0)
    {
        var cancelMessage = new QuickSlCancelMessage
        {
            RequestId = requestId,
            Reason = reason,
            RelatedPlayerId = relatedPlayerId
        };

        foreach (ulong playerId in playerIds.Distinct())
        {
            TrySendHostMessage(hostService, cancelMessage, playerId);
        }
    }

    public void SendInitiatePending(
        INetHostGameService hostService,
        ulong playerId,
        uint clientRequestId)
    {
        TrySendHostMessage(
            hostService,
            new QuickSlInitiatePendingMessage
            {
                ClientRequestId = clientRequestId
            },
            playerId);
    }

    public void SendInitiateResponse(
        INetHostGameService hostService,
        ulong playerId,
        uint clientRequestId,
        uint hostRequestId,
        bool approved,
        QuickSlCancelReason reason,
        bool waitingForOtherPlayers)
    {
        TrySendHostMessage(
            hostService,
            new QuickSlInitiateResponseMessage
            {
                ClientRequestId = clientRequestId,
                HostRequestId = hostRequestId,
                Approved = approved,
                Reason = reason,
                WaitingForOtherPlayers = waitingForOtherPlayers
            },
            playerId);
    }

    public void TrySendHostMessage<T>(INetHostGameService hostService, T message, ulong playerId)
        where T : INetMessage
    {
        if (!hostService.IsConnected)
        {
            ModLogger.Warn($"多人快速 SL：主机网络服务已断开，跳过发送 {typeof(T).Name} 给 {playerId}。");
            return;
        }

        try
        {
            hostService.SendMessage(message, playerId);
        }
        catch (Exception ex)
        {
            ModLogger.Warn($"多人快速 SL：发送 {typeof(T).Name} 给 {playerId} 失败：{ex.Message}");
        }
    }

    public bool IsExecuteSenderValid(ulong senderId)
    {
        INetGameService netService = RunManager.Instance.NetService;
        return netService.Type switch
        {
            NetGameType.Client when netService is INetClientGameService clientService =>
                clientService.NetClient?.HostNetId == senderId,
            NetGameType.Host => false,
            _ => false
        };
    }
}
