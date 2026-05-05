using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace QuickSL.Core;

internal sealed class QuickSlNetworkMessageRouter(QuickSlMultiplayerController controller)
{
    private QuickSlMultiplayerState State => controller.State;

    public void RegisterHandlers(INetGameService netService)
    {
        if (ReferenceEquals(State.RegisteredNetService, netService))
        {
            return;
        }

        UnregisterHandlers();
        State.RegisteredNetService = netService;
        netService.RegisterMessageHandler<QuickSlInitiateMessage>(controller.Host.HandleQuickSlInitiate);
        netService.RegisterMessageHandler<QuickSlInitiatePendingMessage>(controller.Client.HandleQuickSlInitiatePending);
        netService.RegisterMessageHandler<QuickSlInitiateResponseMessage>(controller.Client.HandleQuickSlInitiateResponse);
        netService.RegisterMessageHandler<QuickSlRequestMessage>(controller.Client.HandleQuickSlRequest);
        netService.RegisterMessageHandler<QuickSlVoteMessage>(controller.Host.HandleQuickSlVote);
        netService.RegisterMessageHandler<QuickSlExecuteMessage>(controller.Client.HandleQuickSlExecute);
        netService.RegisterMessageHandler<QuickSlLoadReadyMessage>(controller.Barrier.HandleQuickSlLoadReady);
        netService.RegisterMessageHandler<QuickSlLoadBeginMessage>(controller.Barrier.HandleQuickSlLoadBegin);
        netService.RegisterMessageHandler<QuickSlSetupReadyMessage>(controller.Barrier.HandleQuickSlSetupReady);
        netService.RegisterMessageHandler<QuickSlRunBeginMessage>(controller.Barrier.HandleQuickSlRunBegin);
        netService.RegisterMessageHandler<QuickSlCancelMessage>(controller.Client.HandleQuickSlCancel);
        netService.Disconnected += HandleRegisteredNetServiceDisconnected;
        ModLogger.Debug($"多人快速 SL：已注册网络消息处理器，模式={netService.Type}。");
    }

    private void UnregisterHandlers()
    {
        if (State.RegisteredNetService == null)
        {
            return;
        }

        State.RegisteredNetService.UnregisterMessageHandler<QuickSlInitiateMessage>(controller.Host.HandleQuickSlInitiate);
        State.RegisteredNetService.UnregisterMessageHandler<QuickSlInitiatePendingMessage>(controller.Client.HandleQuickSlInitiatePending);
        State.RegisteredNetService.UnregisterMessageHandler<QuickSlInitiateResponseMessage>(controller.Client.HandleQuickSlInitiateResponse);
        State.RegisteredNetService.UnregisterMessageHandler<QuickSlRequestMessage>(controller.Client.HandleQuickSlRequest);
        State.RegisteredNetService.UnregisterMessageHandler<QuickSlVoteMessage>(controller.Host.HandleQuickSlVote);
        State.RegisteredNetService.UnregisterMessageHandler<QuickSlExecuteMessage>(controller.Client.HandleQuickSlExecute);
        State.RegisteredNetService.UnregisterMessageHandler<QuickSlLoadReadyMessage>(controller.Barrier.HandleQuickSlLoadReady);
        State.RegisteredNetService.UnregisterMessageHandler<QuickSlLoadBeginMessage>(controller.Barrier.HandleQuickSlLoadBegin);
        State.RegisteredNetService.UnregisterMessageHandler<QuickSlSetupReadyMessage>(controller.Barrier.HandleQuickSlSetupReady);
        State.RegisteredNetService.UnregisterMessageHandler<QuickSlRunBeginMessage>(controller.Barrier.HandleQuickSlRunBegin);
        State.RegisteredNetService.UnregisterMessageHandler<QuickSlCancelMessage>(controller.Client.HandleQuickSlCancel);
        State.RegisteredNetService.Disconnected -= HandleRegisteredNetServiceDisconnected;
        State.RegisteredNetService = null;
    }

    private void HandleRegisteredNetServiceDisconnected(NetErrorInfo info)
    {
        controller.HandleRegisteredNetServiceDisconnected(info);
    }
}
