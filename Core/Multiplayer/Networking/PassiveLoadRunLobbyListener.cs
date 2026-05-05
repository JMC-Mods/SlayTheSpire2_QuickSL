using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;

namespace QuickSL.Core;

internal sealed class PassiveLoadRunLobbyListener : ILoadRunLobbyListener
{
    public static PassiveLoadRunLobbyListener Instance { get; } = new();

    public void PlayerConnected(ulong playerId)
    {
        ModLogger.Debug($"多人快速 SL：LoadRunLobby 玩家已连接 {playerId}。");
    }

    public void RemotePlayerDisconnected(ulong playerId)
    {
        ModLogger.Debug($"多人快速 SL：LoadRunLobby 玩家已断开 {playerId}。");
    }

    public Task<bool> ShouldAllowRunToBegin()
    {
        return Task.FromResult(true);
    }

    public void BeginRun()
    {
        ModLogger.Debug("多人快速 SL：LoadRunLobby 收到开始载入通知。");
    }

    public void PlayerReadyChanged(ulong playerId)
    {
        ModLogger.Debug($"多人快速 SL：LoadRunLobby 玩家准备状态变化 {playerId}。");
    }

    public void LocalPlayerDisconnected(NetErrorInfo info)
    {
        ModLogger.Warn($"多人快速 SL：LoadRunLobby 本地连接断开，原因={info.GetReason()}。");
    }
}
