using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace QuickSL.Core;

internal sealed class QuickSlLoadBarrierCoordinator(QuickSlMultiplayerController controller)
{
    private QuickSlMultiplayerState State => controller.State;

    private QuickSlNetworkSender Sender => controller.Sender;

    public void HandleQuickSlLoadReady(QuickSlLoadReadyMessage message, ulong senderId)
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Host)
        {
            return;
        }

        HostLoadBarrierState? barrierState = State.HostLoadBarrierState;
        if (barrierState == null || barrierState.RequestId != message.RequestId)
        {
            ModLogger.Warn($"多人快速 SL：收到过期或未知的载入就绪消息，RequestId={message.RequestId}，Sender={senderId}。");
            return;
        }

        barrierState.MarkReady(senderId);
    }

    public void HandleQuickSlLoadBegin(QuickSlLoadBeginMessage message, ulong senderId)
    {
        if (!Sender.IsExecuteSenderValid(senderId))
        {
            return;
        }

        ClientLoadBarrierState? barrierState = State.ClientLoadBarrierState;
        if (barrierState == null || barrierState.RequestId != message.RequestId)
        {
            ModLogger.Warn($"多人快速 SL：收到过期或未知的开始载入消息，RequestId={message.RequestId}，Sender={senderId}。");
            return;
        }

        barrierState.Begin();
    }

    public void HandleQuickSlSetupReady(QuickSlSetupReadyMessage message, ulong senderId)
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Host)
        {
            return;
        }

        HostLoadBarrierState? barrierState = State.HostSetupBarrierState;
        if (barrierState == null || barrierState.RequestId != message.RequestId)
        {
            ModLogger.Warn($"多人快速 SL：收到过期或未知的重载同步器就绪消息，RequestId={message.RequestId}，Sender={senderId}。");
            return;
        }

        barrierState.MarkReady(senderId);
    }

    public void HandleQuickSlRunBegin(QuickSlRunBeginMessage message, ulong senderId)
    {
        if (!Sender.IsExecuteSenderValid(senderId))
        {
            return;
        }

        ClientLoadBarrierState? barrierState = State.ClientRunBeginBarrierState;
        if (barrierState == null || barrierState.RequestId != message.RequestId)
        {
            ModLogger.Warn($"多人快速 SL：收到过期或未知的重载开始消息，RequestId={message.RequestId}，Sender={senderId}。");
            return;
        }

        barrierState.Begin();
    }

    public async Task WaitForCoordinatedLoadBeginAsync(
        uint requestId,
        INetGameService netService,
        IReadOnlyCollection<ulong> connectedPlayerIds)
    {
        if (netService.Type == NetGameType.Host)
        {
            await WaitForClientsReadyToLoadAsync(requestId, netService, connectedPlayerIds);
            return;
        }

        if (netService.Type == NetGameType.Client)
        {
            await WaitForHostBeginLoadAsync(requestId, netService);
        }
    }

    public HostLoadBarrierState? PrepareHostSetupBarrier(
        uint requestId,
        INetGameService netService,
        IReadOnlyCollection<ulong> connectedPlayerIds)
    {
        if (netService.Type != NetGameType.Host)
        {
            return null;
        }

        ulong[] remotePlayerIds = [.. connectedPlayerIds.Where(playerId => playerId != netService.NetId)];
        if (remotePlayerIds.Length == 0)
        {
            return null;
        }

        var barrierState = new HostLoadBarrierState(requestId, remotePlayerIds);
        State.HostSetupBarrierState = barrierState;
        return barrierState;
    }

    public async Task WaitForCoordinatedRunBeginAsync(
        uint requestId,
        INetGameService netService,
        IReadOnlyCollection<ulong> connectedPlayerIds)
    {
        if (netService.Type == NetGameType.Host)
        {
            await WaitForClientsReadyToRunAsync(requestId, netService, connectedPlayerIds);
            return;
        }

        if (netService.Type == NetGameType.Client)
        {
            await WaitForHostBeginRunAsync(requestId, netService);
        }
    }

    private async Task WaitForClientsReadyToLoadAsync(
        uint requestId,
        INetGameService netService,
        IReadOnlyCollection<ulong> connectedPlayerIds)
    {
        if (netService is not INetHostGameService hostService)
        {
            return;
        }

        ulong[] remotePlayerIds = [.. connectedPlayerIds.Where(playerId => playerId != netService.NetId)];
        if (remotePlayerIds.Length == 0)
        {
            return;
        }

        HostLoadBarrierState barrierState = State.HostLoadBarrierState?.RequestId == requestId
            ? State.HostLoadBarrierState
            : new HostLoadBarrierState(requestId, remotePlayerIds);
        if (!ReferenceEquals(State.HostLoadBarrierState, barrierState))
        {
            State.HostLoadBarrierState = barrierState;
        }

        ModLogger.Debug($"多人快速 SL：等待 {remotePlayerIds.Length} 个客机完成载入准备，RequestId={requestId}。");
        await barrierState.WaitAsync(QuickSlMultiplayerController.LoadBarrierTimeout);

        var beginMessage = new QuickSlLoadBeginMessage
        {
            RequestId = requestId
        };

        foreach (ulong playerId in remotePlayerIds)
        {
            Sender.TrySendHostMessage(hostService, beginMessage, playerId);
        }

        ModLogger.Debug($"多人快速 SL：所有客机已准备完毕，开始同步载入，RequestId={requestId}。");
    }

    private async Task WaitForHostBeginLoadAsync(uint requestId, INetGameService netService)
    {
        var barrierState = new ClientLoadBarrierState(requestId);
        State.ClientLoadBarrierState = barrierState;

        try
        {
            netService.SendMessage(new QuickSlLoadReadyMessage
            {
                RequestId = requestId
            });

            ModLogger.Debug($"多人快速 SL：已向主机报告载入准备完成，RequestId={requestId}。");
            await barrierState.WaitAsync(QuickSlMultiplayerController.LoadBarrierTimeout);
        }
        finally
        {
            if (ReferenceEquals(State.ClientLoadBarrierState, barrierState))
            {
                State.ClientLoadBarrierState = null;
            }
        }
    }

    private async Task WaitForClientsReadyToRunAsync(
        uint requestId,
        INetGameService netService,
        IReadOnlyCollection<ulong> connectedPlayerIds)
    {
        if (netService is not INetHostGameService hostService)
        {
            return;
        }

        ulong[] remotePlayerIds = [.. connectedPlayerIds.Where(playerId => playerId != netService.NetId)];
        if (remotePlayerIds.Length == 0)
        {
            return;
        }

        HostLoadBarrierState barrierState = State.HostSetupBarrierState?.RequestId == requestId
            ? State.HostSetupBarrierState
            : new HostLoadBarrierState(requestId, remotePlayerIds);
        if (!ReferenceEquals(State.HostSetupBarrierState, barrierState))
        {
            State.HostSetupBarrierState = barrierState;
        }

        ModLogger.Debug($"多人快速 SL：等待 {remotePlayerIds.Length} 个客机完成重载同步器注册，RequestId={requestId}。");
        await barrierState.WaitAsync(QuickSlMultiplayerController.LoadBarrierTimeout);

        var beginMessage = new QuickSlRunBeginMessage
        {
            RequestId = requestId
        };

        foreach (ulong playerId in remotePlayerIds)
        {
            Sender.TrySendHostMessage(hostService, beginMessage, playerId);
        }

        ModLogger.Debug($"多人快速 SL：所有客机同步器已就绪，开始同步重载，RequestId={requestId}。");
    }

    private async Task WaitForHostBeginRunAsync(uint requestId, INetGameService netService)
    {
        var barrierState = new ClientLoadBarrierState(requestId);
        State.ClientRunBeginBarrierState = barrierState;

        try
        {
            netService.SendMessage(new QuickSlSetupReadyMessage
            {
                RequestId = requestId
            });

            ModLogger.Debug($"多人快速 SL：已向主机报告重载同步器就绪，RequestId={requestId}。");
            await barrierState.WaitAsync(QuickSlMultiplayerController.LoadBarrierTimeout);
        }
        finally
        {
            if (ReferenceEquals(State.ClientRunBeginBarrierState, barrierState))
            {
                State.ClientRunBeginBarrierState = null;
            }
        }
    }
}
