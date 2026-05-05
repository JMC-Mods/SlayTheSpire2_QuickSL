using JmcModLib.Reflection;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using System.Diagnostics.CodeAnalysis;

namespace QuickSL.Core;

internal sealed class QuickSlMultiplayerContext
{
    private static readonly MemberAccessor CombatSyncCompletionSourceAccessor =
        MemberAccessor.Get(typeof(CombatStateSynchronizer), "_syncCompletionSource");

    public bool TryGetValidatedMultiplayerContext(
        bool requireHost,
        [NotNullWhen(true)]
        out NGame? game,
        [NotNullWhen(true)]
        out RunManager? runManager,
        [NotNullWhen(true)]
        out INetGameService? netService)
    {
        game = NGame.Instance;
        runManager = null;
        netService = null;

        if (game == null)
        {
            ModLogger.Warn("多人快速 SL 失败：NGame 尚未初始化。");
            return false;
        }

        if (game.Transition.InTransition)
        {
            ModLogger.Warn("多人快速 SL 失败：当前正在切换场景。");
            return false;
        }

        runManager = RunManager.Instance;
        if (!runManager.IsInProgress)
        {
            ModLogger.Warn("多人快速 SL 失败：当前不在一局游戏中。");
            return false;
        }

        if (runManager.IsCleaningUp)
        {
            ModLogger.Warn("多人快速 SL 失败：当前局正在清理中。");
            return false;
        }

        netService = runManager.NetService;
        if (!IsMultiplayer(netService.Type))
        {
            ModLogger.Warn($"多人快速 SL 失败：当前不是多人模式，NetService={netService.Type}。");
            return false;
        }

        if (requireHost && netService.Type != NetGameType.Host)
        {
            ModLogger.Warn("多人快速 SL 失败：只有主机可以发起多人 SL。");
            return false;
        }

        if (runManager.RunLobby == null)
        {
            ModLogger.Warn("多人快速 SL 失败：RunLobby 尚未初始化。");
            return false;
        }

        return true;
    }

    public bool TryGetCurrentClientServiceForHost(
        ulong senderId,
        [NotNullWhen(true)]
        out INetClientGameService? clientService)
    {
        clientService = null;

        try
        {
            if (RunManager.Instance?.NetService is INetClientGameService currentClientService &&
                currentClientService.Type == NetGameType.Client &&
                currentClientService.IsConnected &&
                currentClientService.NetClient?.HostNetId == senderId)
            {
                clientService = currentClientService;
                return true;
            }
        }
        catch (Exception ex)
        {
            ModLogger.Debug($"多人快速 SL：检查当前客机网络服务失败：{ex.Message}");
        }

        return false;
    }

    public bool IsCombatStateSyncInProgress(RunManager runManager)
    {
        try
        {
            if (CombatSyncCompletionSourceAccessor.GetValue(runManager.CombatStateSynchronizer) is TaskCompletionSource completionSource)
            {
                return !completionSource.Task.IsCompleted;
            }
        }
        catch (Exception ex)
        {
            ModLogger.Debug($"多人快速 SL：检查多人状态同步状态失败：{ex.Message}");
        }

        return false;
    }

    public HashSet<ulong> GetConnectedRunPlayerIds(RunManager runManager, INetGameService netService)
    {
        var connectedPlayerIds = new HashSet<ulong>();

        if (runManager.RunLobby != null)
        {
            HashSet<ulong>? hostConnectedPeerIds = netService is INetHostGameService hostService
                ? [.. hostService.ConnectedPeers.Select(peer => peer.peerId)]
                : null;

            foreach (ulong playerId in runManager.RunLobby.ConnectedPlayerIds)
            {
                if (playerId == netService.NetId || hostConnectedPeerIds?.Contains(playerId) != false)
                {
                    connectedPlayerIds.Add(playerId);
                }
            }

            if (hostConnectedPeerIds != null && connectedPlayerIds.Count < runManager.RunLobby.ConnectedPlayerIds.Count)
            {
                ModLogger.Info(
                    $"多人快速 SL：本次只同步 {connectedPlayerIds.Count}/{runManager.RunLobby.ConnectedPlayerIds.Count} 个仍在线玩家。");
            }
        }

        connectedPlayerIds.Add(netService.NetId);
        return connectedPlayerIds;
    }

    public bool IsMultiplayer(NetGameType netType)
    {
        return netType is NetGameType.Host or NetGameType.Client;
    }
}
