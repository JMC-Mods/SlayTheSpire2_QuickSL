using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Quality;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Platform;

namespace QuickSL.Core;

internal sealed class DisconnectSuppressingNetGameService(INetGameService inner) : INetGameService
{
    public ulong NetId => inner.NetId;

    public bool IsConnected => inner.IsConnected;

    public bool IsGameLoading => inner.IsGameLoading;

    public NetGameType Type => inner.Type;

    public PlatformType Platform => inner.Platform;

    public event Action<NetErrorInfo>? Disconnected
    {
        add => inner.Disconnected += value;
        remove => inner.Disconnected -= value;
    }

    public void SendMessage<T>(T message, ulong playerId) where T : INetMessage
    {
        inner.SendMessage(message, playerId);
    }

    public void SendMessage<T>(T message) where T : INetMessage
    {
        inner.SendMessage(message);
    }

    public void RegisterMessageHandler<T>(MessageHandlerDelegate<T> messageHandlerDelegate) where T : INetMessage
    {
        inner.RegisterMessageHandler(messageHandlerDelegate);
    }

    public void UnregisterMessageHandler<T>(MessageHandlerDelegate<T> messageHandlerDelegate) where T : INetMessage
    {
        inner.UnregisterMessageHandler(messageHandlerDelegate);
    }

    public void Update()
    {
        inner.Update();
    }

    public void Disconnect(NetError reason, bool now = false)
    {
        ModLogger.Debug($"多人快速 SL：跳过 CleanUp 中的 NetService.Disconnect({reason}, now={now})。");
    }

    public ConnectionStats? GetStatsForPeer(ulong peerId)
    {
        return inner.GetStatsForPeer(peerId);
    }

    public void SetGameLoading(bool isLoading)
    {
        inner.SetGameLoading(isLoading);
    }

    public string? GetRawLobbyIdentifier()
    {
        return inner.GetRawLobbyIdentifier();
    }
}
