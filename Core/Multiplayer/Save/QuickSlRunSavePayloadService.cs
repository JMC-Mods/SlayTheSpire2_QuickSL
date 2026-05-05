using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using System.Text;

namespace QuickSL.Core;

internal sealed class QuickSlRunSavePayloadService
{
    public async Task<SerializableRun?> LoadLocalMultiplayerRunSaveAsync(INetGameService netService)
    {
        try
        {
            SaveManager saveManager = SaveManager.Instance;
            Task? currentSaveTask = saveManager.CurrentRunSaveTask;
            if (currentSaveTask != null)
            {
                ModLogger.Info("多人快速 SL：等待当前存档任务完成。");
                await currentSaveTask;
            }

            if (!saveManager.HasMultiplayerRunSave)
            {
                ModLogger.Warn("多人快速 SL 失败：没有找到多人当前局存档。");
                return null;
            }

            ReadSaveResult<SerializableRun> readResult =
                saveManager.LoadAndCanonicalizeMultiplayerRunSave(netService.NetId);
            if (!readResult.Success || readResult.SaveData == null)
            {
                ModLogger.Warn($"多人快速 SL 失败：读取多人存档失败，状态={readResult.Status}。");
                return null;
            }

            return readResult.SaveData;
        }
        catch (Exception ex)
        {
            ModLogger.Error("多人快速 SL：读取本地多人存档失败。", ex);
            return null;
        }
    }

    public string? TrySerializeRunSavePayload(SerializableRun runSave)
    {
        try
        {
            string json = JsonSerializationUtility.ToJson(runSave);
            int byteCount = Encoding.UTF8.GetByteCount(json);
            if (byteCount > QuickSlExecuteMessage.MaxRunSaveJsonBytes)
            {
                ModLogger.Warn(
                    $"多人快速 SL 失败：多人存档同步数据过大，大小={byteCount} 字节，上限={QuickSlExecuteMessage.MaxRunSaveJsonBytes} 字节。");
                return null;
            }

            ModLogger.Debug($"多人快速 SL：已生成主机存档同步数据，大小={byteCount} 字节。");
            return json;
        }
        catch (Exception ex)
        {
            ModLogger.Error("多人快速 SL：序列化主机多人存档失败。", ex);
            return null;
        }
    }

    public SerializableRun? TryDeserializeRunSavePayload(string runSaveJson)
    {
        if (string.IsNullOrWhiteSpace(runSaveJson))
        {
            ModLogger.Warn("多人快速 SL：主机未发送多人存档数据。");
            return null;
        }

        ReadSaveResult<SerializableRun> readResult = JsonSerializationUtility.FromJson<SerializableRun>(runSaveJson);
        if (!readResult.Success || readResult.SaveData == null)
        {
            ModLogger.Warn($"多人快速 SL：解析主机多人存档失败，状态={readResult.Status}。");
            return null;
        }

        return readResult.SaveData;
    }

    public async Task<SerializableRun?> PrepareRemoteRunSaveForLocalLoadAsync(
        SerializableRun hostRunSave,
        INetGameService netService)
    {
        if (netService.Type == NetGameType.Client)
        {
            await TryMergeLocalPreFinishedRoomRewardsAsync(hostRunSave, netService);
        }

        return TryCanonicalizeRunSave(hostRunSave, netService.NetId);
    }

    private SerializableRun? TryCanonicalizeRunSave(SerializableRun runSave, ulong localPlayerId)
    {
        try
        {
            return RunManager.CanonicalizeSave(runSave, localPlayerId);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"多人快速 SL：主机存档不包含本地玩家 {localPlayerId}，无法载入。", ex);
            return null;
        }
    }

    private async Task TryMergeLocalPreFinishedRoomRewardsAsync(
        SerializableRun hostRunSave,
        INetGameService netService)
    {
        SerializableRoom? hostRoom = hostRunSave.PreFinishedRoom;
        if (hostRoom == null)
        {
            return;
        }

        SerializableRun? localRunSave = await LoadLocalMultiplayerRunSaveAsync(netService);
        SerializableRoom? localRoom = localRunSave?.PreFinishedRoom;
        if (localRoom == null)
        {
            return;
        }

        if (!IsSamePreFinishedRoom(hostRoom, localRoom))
        {
            ModLogger.Debug("多人快速 SL：本机预完成房间与主机存档不一致，跳过本地奖励合并。");
            return;
        }

        if (!localRoom.ExtraRewards.TryGetValue(netService.NetId, out List<SerializableReward>? localRewards) ||
            localRewards.Count == 0)
        {
            return;
        }

        hostRoom.ExtraRewards[netService.NetId] = [.. localRewards];
        ModLogger.Info($"多人快速 SL：已从本机存档补回本地预完成房间奖励，数量={localRewards.Count}。");
    }

    private static bool IsSamePreFinishedRoom(SerializableRoom hostRoom, SerializableRoom localRoom)
    {
        return hostRoom.RoomType == localRoom.RoomType &&
            hostRoom.IsPreFinished == localRoom.IsPreFinished &&
            Equals(hostRoom.EncounterId, localRoom.EncounterId) &&
            Equals(hostRoom.EventId, localRoom.EventId) &&
            Equals(hostRoom.ParentEventId, localRoom.ParentEventId);
    }
}
