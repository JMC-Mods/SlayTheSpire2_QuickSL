using JmcModLib.Prefabs;
using JmcModLib.Reflection;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Quality;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace QuickSL.Core;

internal static class MultiplayerQuickSlCoordinator
{
    private static readonly TimeSpan VoteTimeout = TimeSpan.FromSeconds(30);

    private static readonly SemaphoreSlim ReloadLock = new(1, 1);

    private static readonly MemberAccessor NetServiceAccessor =
        MemberAccessor.Get(typeof(RunManager), nameof(RunManager.NetService));

    private static readonly MemberAccessor RunLobbyConnectedPlayerIdsAccessor =
        MemberAccessor.Get(typeof(RunLobby), "_connectedPlayerIds");

    private static uint nextRequestId;
    private static INetGameService? registeredNetService;
    private static HostVoteState? hostVoteState;
    private static uint? activeClientRequestId;

    public static void EnsureHandlersRegistered()
    {
        try
        {
            if (RunManager.Instance?.NetService is not { } netService)
            {
                return;
            }

            if (!IsMultiplayer(netService.Type))
            {
                return;
            }

            RegisterHandlers(netService);
        }
        catch (Exception ex)
        {
            ModLogger.Error("多人快速 SL：注册网络消息处理器失败。", ex);
        }
    }

    public static async Task RunHostAsync()
    {
        EnsureHandlersRegistered();

        if (!TryGetValidatedMultiplayerContext(requireHost: true, out _, out RunManager? runManager, out INetGameService? netService))
        {
            return;
        }

        if (netService is not INetHostGameService hostService)
        {
            ModLogger.Warn($"多人快速 SL 失败：当前不是主机模式，NetService={netService.Type}。");
            return;
        }

        if (hostVoteState != null)
        {
            ModLogger.Warn("多人快速 SL 已在等待客机确认，忽略重复触发。");
            return;
        }

        uint requestId = CreateRequestId();
        HashSet<ulong> connectedPlayerIds = GetConnectedRunPlayerIds(runManager, netService);
        ulong[] remotePlayerIds = [.. connectedPlayerIds.Where(playerId => playerId != netService.NetId)];

        if (!QuickSlSettings.RequireMultiplayerClientConfirmation || remotePlayerIds.Length == 0)
        {
            SerializableRun? runSave = await LoadLocalMultiplayerRunSaveAsync(netService);
            if (runSave == null)
            {
                return;
            }

            if (remotePlayerIds.Length > 0)
            {
                string? runSaveJson = TrySerializeRunSavePayload(runSave);
                if (runSaveJson == null)
                {
                    return;
                }

                SendExecute(hostService, requestId, remotePlayerIds, connectedPlayerIds, runSaveJson);
                ModLogger.Info($"多人快速 SL：已通知 {remotePlayerIds.Length} 个客机同步执行。");
            }

            await ExecuteLocalMultiplayerQuickSlAsync(requestId, connectedPlayerIds, runSave);
            return;
        }

        using var timeoutCancel = new CancellationTokenSource();
        var voteState = new HostVoteState(requestId, connectedPlayerIds, remotePlayerIds, timeoutCancel);
        hostVoteState = voteState;

        try
        {
            var requestMessage = new QuickSlRequestMessage
            {
                RequestId = requestId,
                RequiresClientConfirmation = true
            };

            foreach (ulong playerId in remotePlayerIds)
            {
                hostService.SendMessage(requestMessage, playerId);
            }

            _ = RunVoteTimeoutAsync(voteState);
            ModLogger.Info($"多人快速 SL：已向 {remotePlayerIds.Length} 个客机发送确认请求，请等待所有人同意。");

            bool approved = await voteState.Completion.Task;
            if (!approved)
            {
                SendCancel(hostService, voteState);
                ModLogger.Warn($"多人快速 SL 已取消，原因={voteState.CancelReason}。");
                return;
            }

            SerializableRun? runSave = await LoadLocalMultiplayerRunSaveAsync(netService);
            if (runSave == null)
            {
                voteState.Cancel(QuickSlCancelReason.InvalidState);
                SendCancel(hostService, voteState);
                return;
            }

            string? runSaveJson = TrySerializeRunSavePayload(runSave);
            if (runSaveJson == null)
            {
                voteState.Cancel(QuickSlCancelReason.InvalidState);
                SendCancel(hostService, voteState);
                return;
            }

            SendExecute(hostService, requestId, remotePlayerIds, voteState.ConnectedPlayerIds, runSaveJson);
            await ExecuteLocalMultiplayerQuickSlAsync(requestId, voteState.ConnectedPlayerIds, runSave);
        }
        finally
        {
            timeoutCancel.Cancel();
            if (ReferenceEquals(hostVoteState, voteState))
            {
                hostVoteState = null;
            }
        }
    }

    private static void RegisterHandlers(INetGameService netService)
    {
        if (ReferenceEquals(registeredNetService, netService))
        {
            return;
        }

        UnregisterHandlers();
        registeredNetService = netService;
        netService.RegisterMessageHandler<QuickSlRequestMessage>(HandleQuickSlRequest);
        netService.RegisterMessageHandler<QuickSlVoteMessage>(HandleQuickSlVote);
        netService.RegisterMessageHandler<QuickSlExecuteMessage>(HandleQuickSlExecute);
        netService.RegisterMessageHandler<QuickSlCancelMessage>(HandleQuickSlCancel);
        netService.Disconnected += HandleRegisteredNetServiceDisconnected;
        ModLogger.Debug($"多人快速 SL：已注册网络消息处理器，模式={netService.Type}。");
    }

    private static void UnregisterHandlers()
    {
        if (registeredNetService == null)
        {
            return;
        }

        registeredNetService.UnregisterMessageHandler<QuickSlRequestMessage>(HandleQuickSlRequest);
        registeredNetService.UnregisterMessageHandler<QuickSlVoteMessage>(HandleQuickSlVote);
        registeredNetService.UnregisterMessageHandler<QuickSlExecuteMessage>(HandleQuickSlExecute);
        registeredNetService.UnregisterMessageHandler<QuickSlCancelMessage>(HandleQuickSlCancel);
        registeredNetService.Disconnected -= HandleRegisteredNetServiceDisconnected;
        registeredNetService = null;
    }

    private static void HandleRegisteredNetServiceDisconnected(NetErrorInfo info)
    {
        hostVoteState?.Cancel(QuickSlCancelReason.InvalidState);
        hostVoteState = null;
        activeClientRequestId = null;
        ModLogger.Debug($"多人快速 SL：当前网络服务已断开，原因={info.GetReason()}。");
    }

    private static void HandleQuickSlRequest(QuickSlRequestMessage message, ulong senderId)
    {
        if (!TryGetValidatedMultiplayerContext(requireHost: false, out _, out _, out INetGameService? netService))
        {
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

        if (activeClientRequestId.HasValue)
        {
            ModLogger.Warn("多人快速 SL：已有确认弹窗正在等待处理，拒绝新的请求。");
            SendClientVote(message.RequestId, approved: false);
            return;
        }

        activeClientRequestId = message.RequestId;
        _ = RespondToHostRequestAsync(message);
    }

    private static void HandleQuickSlVote(QuickSlVoteMessage message, ulong senderId)
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Host)
        {
            return;
        }

        HostVoteState? voteState = hostVoteState;
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
            voteState.Cancel(QuickSlCancelReason.Rejected);
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

    private static void HandleQuickSlExecute(QuickSlExecuteMessage message, ulong senderId)
    {
        if (!IsExecuteSenderValid(senderId))
        {
            ModLogger.Warn($"多人快速 SL：收到非法执行消息，Sender={senderId}，RequestId={message.RequestId}。");
            return;
        }

        activeClientRequestId = null;
        SerializableRun? runSave = TryDeserializeRunSavePayload(message.RunSaveJson);
        if (runSave == null)
        {
            ModLogger.Warn($"多人快速 SL：主机执行消息缺少有效存档，RequestId={message.RequestId}。");
            return;
        }

        _ = ExecuteLocalMultiplayerQuickSlAsync(message.RequestId, message.ConnectedPlayerIds, runSave);
    }

    private static void HandleQuickSlCancel(QuickSlCancelMessage message, ulong senderId)
    {
        if (!IsExecuteSenderValid(senderId))
        {
            return;
        }

        if (activeClientRequestId == message.RequestId)
        {
            activeClientRequestId = null;
        }

        ModLogger.Warn($"多人快速 SL：主机取消了本次 SL，原因={message.Reason}。");
    }

    private static async Task RespondToHostRequestAsync(QuickSlRequestMessage message)
    {
        bool approved = false;

        try
        {
            if (!message.RequiresClientConfirmation)
            {
                approved = true;
            }
            else if (JmcConfirmationPopup.IsAvailable)
            {
                approved = await JmcConfirmationPopup.ShowConfirmationAsync(
                    new LocString("settings_ui", "EXTENSION.QUICKSL.MULTIPLAYER_CONFIRM.title"),
                    new LocString("settings_ui", "EXTENSION.QUICKSL.MULTIPLAYER_CONFIRM.body"),
                    new LocString("settings_ui", "EXTENSION.QUICKSL.MULTIPLAYER_CONFIRM.confirm"),
                    new LocString("settings_ui", "EXTENSION.QUICKSL.MULTIPLAYER_CONFIRM.cancel"));
            }
            else
            {
                ModLogger.Warn("多人快速 SL：确认弹窗不可用，已拒绝本次请求。");
            }

            if (activeClientRequestId != message.RequestId)
            {
                ModLogger.Info($"多人快速 SL：确认请求 {message.RequestId} 已失效，不再发送回复。");
                return;
            }

            SendClientVote(message.RequestId, approved);
            ModLogger.Info($"多人快速 SL：已向主机发送确认结果，Approved={approved}。");
        }
        catch (Exception ex)
        {
            ModLogger.Error("多人快速 SL：处理主机确认请求失败。", ex);
            if (activeClientRequestId == message.RequestId)
            {
                SendClientVote(message.RequestId, approved: false);
            }
        }
        finally
        {
            if (activeClientRequestId == message.RequestId)
            {
                activeClientRequestId = null;
            }
        }
    }

    private static void SendClientVote(uint requestId, bool approved)
    {
        INetGameService netService = RunManager.Instance.NetService;
        netService.SendMessage(new QuickSlVoteMessage
        {
            RequestId = requestId,
            Approved = approved
        });
    }

    private static async Task RunVoteTimeoutAsync(HostVoteState voteState)
    {
        try
        {
            await Task.Delay(VoteTimeout, voteState.TimeoutCancel.Token);
            if (ReferenceEquals(hostVoteState, voteState))
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

    private static async Task<SerializableRun?> LoadLocalMultiplayerRunSaveAsync(INetGameService netService)
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

    private static string? TrySerializeRunSavePayload(SerializableRun runSave)
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

    private static SerializableRun? TryDeserializeRunSavePayload(string runSaveJson)
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

    private static SerializableRun? TryCanonicalizeRunSave(SerializableRun runSave, ulong localPlayerId)
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

    private static async Task ExecuteLocalMultiplayerQuickSlAsync(
        uint requestId,
        IReadOnlyCollection<ulong>? connectedPlayerIdsOverride = null,
        SerializableRun? runSaveOverride = null)
    {
        if (!await ReloadLock.WaitAsync(TimeSpan.Zero))
        {
            ModLogger.Warn("多人快速 SL 已在执行中，忽略重复执行消息。");
            return;
        }

        bool fadedOut = false;
        bool cleanedUp = false;
        LoadRunLobby? loadLobby = null;

        try
        {
            if (!TryGetValidatedMultiplayerContext(requireHost: false, out NGame? game, out RunManager? runManager, out INetGameService? originalNetService))
            {
                return;
            }

            HashSet<ulong> connectedPlayerIds = connectedPlayerIdsOverride == null
                ? GetConnectedRunPlayerIds(runManager, originalNetService)
                : [.. connectedPlayerIdsOverride];
            connectedPlayerIds.Add(originalNetService.NetId);

            SerializableRun? runSave = runSaveOverride == null
                ? await LoadLocalMultiplayerRunSaveAsync(originalNetService)
                : TryCanonicalizeRunSave(runSaveOverride, originalNetService.NetId);
            if (runSave == null)
            {
                return;
            }

            RunState runState = RunState.FromSerializable(runSave);

            ModLogger.Info($"多人快速 SL：执行同步重载，RequestId={requestId}，在线玩家数={connectedPlayerIds.Count}。");
            runManager.ActionQueueSet.Reset();
            NRunMusicController.Instance?.StopMusic();

            await game.Transition.FadeOut();
            fadedOut = true;

            var protectedNetService = new DisconnectSuppressingNetGameService(originalNetService);
            NetServiceAccessor.SetValue(runManager, protectedNetService);
            try
            {
                runManager.CleanUp();
                cleanedUp = true;
            }
            finally
            {
                NetServiceAccessor.SetValue(runManager, originalNetService);
            }

            loadLobby = new LoadRunLobby(originalNetService, PassiveLoadRunLobbyListener.Instance, runSave);
            AddConnectedPlayersToLoadLobby(loadLobby, originalNetService, connectedPlayerIds);
            game.RemoteCursorContainer.Initialize(loadLobby.InputSynchronizer, loadLobby.ConnectedPlayerIds);
            game.ReactionContainer.InitializeNetworking(loadLobby.NetService);

            runManager.SetUpSavedMultiPlayer(runState, loadLobby);
            KeepOnlyConnectedPlayersInRunLobby(runManager, loadLobby.ConnectedPlayerIds);
            EnsureHandlersRegistered();

            await game.LoadRun(runState, runSave.PreFinishedRoom);
            loadLobby.CleanUp(disconnectSession: false);
            loadLobby = null;

            await game.Transition.FadeIn();
            ModLogger.Info($"多人快速 SL 完成，RequestId={requestId}。");
        }
        catch (Exception ex)
        {
            ModLogger.Error("多人快速 SL 执行失败。", ex);
            loadLobby?.CleanUp(disconnectSession: false);
            await TryRecoverAsync(fadedOut, cleanedUp);
        }
        finally
        {
            ReloadLock.Release();
        }
    }

    private static bool TryGetValidatedMultiplayerContext(
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

    private static HashSet<ulong> GetConnectedRunPlayerIds(RunManager runManager, INetGameService netService)
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

    private static void AddConnectedPlayersToLoadLobby(
        LoadRunLobby loadLobby,
        INetGameService netService,
        IEnumerable<ulong> connectedPlayerIds)
    {
        if (netService.Type == NetGameType.Host)
        {
            loadLobby.AddLocalHostPlayer();
        }

        foreach (ulong playerId in connectedPlayerIds)
        {
            loadLobby.ConnectedPlayerIds.Add(playerId);
        }
    }

    private static void KeepOnlyConnectedPlayersInRunLobby(RunManager runManager, IReadOnlySet<ulong> connectedPlayerIds)
    {
        if (runManager.RunLobby == null)
        {
            ModLogger.Warn("多人快速 SL：RunLobby 尚未初始化，无法修正已连接玩家列表。");
            return;
        }

        if (RunLobbyConnectedPlayerIdsAccessor.GetValue(runManager.RunLobby) is not HashSet<ulong> runLobbyConnectedPlayerIds)
        {
            ModLogger.Warn("多人快速 SL：读取 RunLobby 已连接玩家列表失败。");
            return;
        }

        ulong[] disconnectedPlayerIds =
        [
            .. runLobbyConnectedPlayerIds
                .Where(playerId => !connectedPlayerIds.Contains(playerId))
        ];

        foreach (ulong playerId in disconnectedPlayerIds)
        {
            runLobbyConnectedPlayerIds.Remove(playerId);
            runManager.InputSynchronizer.OnPlayerDisconnected(playerId);
        }

        if (disconnectedPlayerIds.Length > 0)
        {
            ModLogger.Info($"多人快速 SL：已将 {disconnectedPlayerIds.Length} 个未连接玩家从本次加载同步等待中移除。");
        }
    }

    private static void SendExecute(
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
            hostService.SendMessage(executeMessage, playerId);
        }
    }

    private static void SendCancel(INetHostGameService hostService, HostVoteState voteState)
    {
        var cancelMessage = new QuickSlCancelMessage
        {
            RequestId = voteState.RequestId,
            Reason = voteState.CancelReason
        };

        foreach (ulong playerId in voteState.ExpectedVotes)
        {
            hostService.SendMessage(cancelMessage, playerId);
        }
    }

    private static bool IsExecuteSenderValid(ulong senderId)
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

    private static bool IsMultiplayer(NetGameType netType)
    {
        return netType is NetGameType.Host or NetGameType.Client;
    }

    private static uint CreateRequestId()
    {
        uint requestId = unchecked(++nextRequestId);
        return requestId == 0 ? unchecked(++nextRequestId) : requestId;
    }

    private static async Task TryRecoverAsync(bool fadedOut, bool cleanedUp)
    {
        try
        {
            if (NGame.Instance is not { } game)
            {
                return;
            }

            if (cleanedUp)
            {
                await game.ReturnToMainMenu();
                return;
            }

            if (fadedOut)
            {
                await game.Transition.FadeIn();
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error("多人快速 SL 失败后恢复界面时再次出错。", ex);
        }
    }

    private sealed class HostVoteState
    {
        public HostVoteState(
            uint requestId,
            IEnumerable<ulong> connectedPlayerIds,
            IEnumerable<ulong> expectedVotes,
            CancellationTokenSource timeoutCancel)
        {
            RequestId = requestId;
            ConnectedPlayerIds = [.. connectedPlayerIds];
            ExpectedVotes = [.. expectedVotes];
            TimeoutCancel = timeoutCancel;
        }

        public uint RequestId { get; }

        public HashSet<ulong> ConnectedPlayerIds { get; }

        public HashSet<ulong> ExpectedVotes { get; }

        public HashSet<ulong> ApprovedVotes { get; } = [];

        public QuickSlCancelReason CancelReason { get; private set; } = QuickSlCancelReason.InvalidState;

        public CancellationTokenSource TimeoutCancel { get; }

        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Cancel(QuickSlCancelReason reason)
        {
            CancelReason = reason;
            Completion.TrySetResult(false);
        }
    }

    private sealed class DisconnectSuppressingNetGameService(INetGameService inner) : INetGameService
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

    private sealed class PassiveLoadRunLobbyListener : ILoadRunLobbyListener
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
}
