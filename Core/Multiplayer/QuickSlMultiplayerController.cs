using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using QuickSL.UI;

namespace QuickSL.Core;

internal sealed class QuickSlMultiplayerController
{
    internal static readonly TimeSpan VoteTimeout = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan LoadBarrierTimeout = TimeSpan.FromSeconds(30);

    internal QuickSlMultiplayerController()
    {
        State = new QuickSlMultiplayerState();
        Context = new QuickSlMultiplayerContext();
        Sender = new QuickSlNetworkSender();
        SavePayload = new QuickSlRunSavePayloadService();
        Barrier = new QuickSlLoadBarrierCoordinator(this);
        Reloader = new QuickSlMultiplayerReloader(this);
        Host = new QuickSlHostFlow(this);
        Client = new QuickSlClientFlow(this);
        Router = new QuickSlNetworkMessageRouter(this);
    }

    internal QuickSlMultiplayerState State { get; }

    internal QuickSlMultiplayerContext Context { get; }

    internal QuickSlNetworkSender Sender { get; }

    internal QuickSlRunSavePayloadService SavePayload { get; }

    internal QuickSlLoadBarrierCoordinator Barrier { get; }

    internal QuickSlMultiplayerReloader Reloader { get; }

    internal QuickSlHostFlow Host { get; }

    internal QuickSlClientFlow Client { get; }

    private QuickSlNetworkMessageRouter Router { get; }

    public void EnsureHandlersRegistered()
    {
        try
        {
            if (RunManager.Instance?.NetService is not { } netService)
            {
                return;
            }

            if (!Context.IsMultiplayer(netService.Type))
            {
                return;
            }

            Router.RegisterHandlers(netService);
        }
        catch (Exception ex)
        {
            ModLogger.Error("多人快速 SL：注册网络消息处理器失败。", ex);
        }
    }

    public async Task RunHostAsync()
    {
        EnsureHandlersRegistered();
        await Host.RunHostAsync();
    }

    public Task RunClientAsync()
    {
        EnsureHandlersRegistered();
        return Client.RunClientAsync();
    }

    internal uint CreateRequestId()
    {
        uint requestId = unchecked(++State.NextRequestId);
        return requestId == 0 ? unchecked(++State.NextRequestId) : requestId;
    }

    internal void HandleRegisteredNetServiceDisconnected(NetErrorInfo info)
    {
        State.ResetForDisconnected();
        QuickSlPopupService.CloseAllWaiting();
        ModLogger.Debug($"多人快速 SL：当前网络服务已断开，原因={info.GetReason()}。");
    }
}
