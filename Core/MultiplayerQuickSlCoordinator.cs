using JmcModLib.Prefabs;
using JmcModLib.Reflection;
using JmcModLib.Utils;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Multiplayer;
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

    private static readonly TimeSpan LoadBarrierTimeout = TimeSpan.FromSeconds(30);

    private static readonly SemaphoreSlim ReloadLock = new(1, 1);

    private static readonly MemberAccessor NetServiceAccessor =
        MemberAccessor.Get(typeof(RunManager), nameof(RunManager.NetService));

    private static readonly MemberAccessor RunLobbyConnectedPlayerIdsAccessor =
        MemberAccessor.Get(typeof(RunLobby), "_connectedPlayerIds");

    private static readonly MemberAccessor CombatSyncCompletionSourceAccessor =
        MemberAccessor.Get(typeof(CombatStateSynchronizer), "_syncCompletionSource");

    private static uint nextRequestId;
    private static INetGameService? registeredNetService;
    private static HostInitiateState? hostInitiateState;
    private static HostVoteState? hostVoteState;
    private static HostLoadBarrierState? hostLoadBarrierState;
    private static HostLoadBarrierState? hostSetupBarrierState;
    private static ClientLoadBarrierState? clientLoadBarrierState;
    private static ClientLoadBarrierState? clientRunBeginBarrierState;
    private static ClientInitiateState? clientInitiateState;
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

        if (!TryPrepareHostQuickSl(out _, out INetHostGameService? hostService, out HashSet<ulong>? connectedPlayerIds))
        {
            return;
        }

        await RunApprovedHostQuickSlAsync(
            hostService,
            CreateRequestId(),
            connectedPlayerIds,
            initiatingClientId: null);
    }

    public static Task RunClientAsync()
    {
        EnsureHandlersRegistered();

        if (!TryGetValidatedMultiplayerContext(requireHost: false, out _, out RunManager? runManager, out INetGameService? netService))
        {
            return Task.CompletedTask;
        }

        if (IsCombatStateSyncInProgress(runManager))
        {
            ModLogger.Warn("多人快速 SL 失败：当前多人状态同步尚未完成，请稍后再试。");
            return Task.CompletedTask;
        }

        if (netService.Type != NetGameType.Client || netService is not INetClientGameService clientService)
        {
            ModLogger.Warn($"多人快速 SL 失败：当前不是客机模式，NetService={netService.Type}。");
            return Task.CompletedTask;
        }

        if (clientInitiateState != null)
        {
            ModLogger.Warn("多人快速 SL：已向主机发起请求，正在等待主机处理。");
            return Task.CompletedTask;
        }

        if (activeClientRequestId.HasValue)
        {
            ModLogger.Warn("多人快速 SL：已有主机确认请求正在等待处理，暂不重复发起。");
            return Task.CompletedTask;
        }

        uint clientRequestId = CreateRequestId();
        var initiateState = new ClientInitiateState(clientRequestId);
        clientInitiateState = initiateState;

        try
        {
            clientService.SendMessage(new QuickSlInitiateMessage
            {
                ClientRequestId = clientRequestId
            });

            ModLogger.Info($"多人快速 SL：已向主机发起 SL 请求，ClientRequestId={clientRequestId}。");
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(clientInitiateState, initiateState))
            {
                clientInitiateState = null;
            }

            ModLogger.Warn($"多人快速 SL：向主机发送发起请求失败：{ex.Message}");
        }

        return Task.CompletedTask;
    }

    private static void RegisterHandlers(INetGameService netService)
    {
        if (ReferenceEquals(registeredNetService, netService))
        {
            return;
        }

        UnregisterHandlers();
        registeredNetService = netService;
        netService.RegisterMessageHandler<QuickSlInitiateMessage>(HandleQuickSlInitiate);
        netService.RegisterMessageHandler<QuickSlInitiateResponseMessage>(HandleQuickSlInitiateResponse);
        netService.RegisterMessageHandler<QuickSlRequestMessage>(HandleQuickSlRequest);
        netService.RegisterMessageHandler<QuickSlVoteMessage>(HandleQuickSlVote);
        netService.RegisterMessageHandler<QuickSlExecuteMessage>(HandleQuickSlExecute);
        netService.RegisterMessageHandler<QuickSlLoadReadyMessage>(HandleQuickSlLoadReady);
        netService.RegisterMessageHandler<QuickSlLoadBeginMessage>(HandleQuickSlLoadBegin);
        netService.RegisterMessageHandler<QuickSlSetupReadyMessage>(HandleQuickSlSetupReady);
        netService.RegisterMessageHandler<QuickSlRunBeginMessage>(HandleQuickSlRunBegin);
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

        registeredNetService.UnregisterMessageHandler<QuickSlInitiateMessage>(HandleQuickSlInitiate);
        registeredNetService.UnregisterMessageHandler<QuickSlInitiateResponseMessage>(HandleQuickSlInitiateResponse);
        registeredNetService.UnregisterMessageHandler<QuickSlRequestMessage>(HandleQuickSlRequest);
        registeredNetService.UnregisterMessageHandler<QuickSlVoteMessage>(HandleQuickSlVote);
        registeredNetService.UnregisterMessageHandler<QuickSlExecuteMessage>(HandleQuickSlExecute);
        registeredNetService.UnregisterMessageHandler<QuickSlLoadReadyMessage>(HandleQuickSlLoadReady);
        registeredNetService.UnregisterMessageHandler<QuickSlLoadBeginMessage>(HandleQuickSlLoadBegin);
        registeredNetService.UnregisterMessageHandler<QuickSlSetupReadyMessage>(HandleQuickSlSetupReady);
        registeredNetService.UnregisterMessageHandler<QuickSlRunBeginMessage>(HandleQuickSlRunBegin);
        registeredNetService.UnregisterMessageHandler<QuickSlCancelMessage>(HandleQuickSlCancel);
        registeredNetService.Disconnected -= HandleRegisteredNetServiceDisconnected;
        registeredNetService = null;
    }

    private static void HandleRegisteredNetServiceDisconnected(NetErrorInfo info)
    {
        hostInitiateState = null;
        hostVoteState?.Cancel(QuickSlCancelReason.InvalidState);
        hostVoteState = null;
        hostLoadBarrierState?.Cancel();
        hostLoadBarrierState = null;
        hostSetupBarrierState?.Cancel();
        hostSetupBarrierState = null;
        clientLoadBarrierState?.Cancel();
        clientLoadBarrierState = null;
        clientRunBeginBarrierState?.Cancel();
        clientRunBeginBarrierState = null;
        clientInitiateState = null;
        activeClientRequestId = null;
        ModLogger.Debug($"多人快速 SL：当前网络服务已断开，原因={info.GetReason()}。");
    }

    private static void HandleQuickSlInitiate(QuickSlInitiateMessage message, ulong senderId)
    {
        if (RunManager.Instance?.NetService is INetHostGameService currentHostService &&
            currentHostService.Type == NetGameType.Host &&
            !QuickSlSettings.AllowClientInitiatedQuickSl)
        {
            SendInitiateResponse(
                currentHostService,
                senderId,
                message.ClientRequestId,
                hostRequestId: 0,
                approved: false,
                QuickSlCancelReason.Disabled);
            ModLogger.Warn($"多人快速 SL：主机设置不允许客机发起，已拒绝玩家 {senderId} 的请求。");
            return;
        }

        if (!TryPrepareHostQuickSl(out _, out INetHostGameService? hostService, out HashSet<ulong>? connectedPlayerIds))
        {
            if (RunManager.Instance?.NetService is INetHostGameService fallbackHostService &&
                fallbackHostService.Type == NetGameType.Host)
            {
                SendInitiateResponse(
                    fallbackHostService,
                    senderId,
                    message.ClientRequestId,
                    hostRequestId: 0,
                    approved: false,
                    QuickSlCancelReason.InvalidState);
            }

            return;
        }

        if (senderId == hostService.NetId || !connectedPlayerIds.Contains(senderId))
        {
            SendInitiateResponse(
                hostService,
                senderId,
                message.ClientRequestId,
                hostRequestId: 0,
                approved: false,
                QuickSlCancelReason.InvalidState);
            ModLogger.Warn($"多人快速 SL：收到非当前客机 {senderId} 的发起请求，已拒绝。");
            return;
        }

        uint hostRequestId = CreateRequestId();
        var initiateState = new HostInitiateState(message.ClientRequestId, hostRequestId, senderId);
        hostInitiateState = initiateState;

        _ = RespondToClientInitiateAsync(hostService, connectedPlayerIds, initiateState);
    }

    private static void HandleQuickSlInitiateResponse(QuickSlInitiateResponseMessage message, ulong senderId)
    {
        if (!IsExecuteSenderValid(senderId))
        {
            ModLogger.Warn($"多人快速 SL：收到非法主机确认回复，Sender={senderId}，ClientRequestId={message.ClientRequestId}。");
            return;
        }

        ClientInitiateState? initiateState = clientInitiateState;
        if (initiateState == null || initiateState.ClientRequestId != message.ClientRequestId)
        {
            ModLogger.Warn($"多人快速 SL：收到过期或未知的主机确认回复，ClientRequestId={message.ClientRequestId}。");
            return;
        }

        if (!message.Approved)
        {
            clientInitiateState = null;
            ModLogger.Warn($"多人快速 SL：主机拒绝了客机发起请求，原因={message.Reason}。");
            return;
        }

        initiateState.HostRequestId = message.HostRequestId;
        ModLogger.Info($"多人快速 SL：主机已同意客机发起请求，等待同步执行，RequestId={message.HostRequestId}。");
    }

    private static void HandleQuickSlRequest(QuickSlRequestMessage message, ulong senderId)
    {
        if (!TryGetValidatedMultiplayerContext(requireHost: false, out _, out RunManager? runManager, out INetGameService? netService))
        {
            if (TryGetCurrentClientServiceForHost(senderId, out _))
            {
                SendClientVote(message.RequestId, approved: false);
                ModLogger.Warn($"多人快速 SL：当前状态无法响应主机请求，已拒绝 RequestId={message.RequestId}。");
            }

            return;
        }

        if (IsCombatStateSyncInProgress(runManager))
        {
            ModLogger.Warn("多人快速 SL：当前多人状态同步尚未完成，已拒绝主机请求。");
            SendClientVote(message.RequestId, approved: false);
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
        clientInitiateState = null;
        SerializableRun? runSave = TryDeserializeRunSavePayload(message.RunSaveJson);
        if (runSave == null)
        {
            ModLogger.Warn($"多人快速 SL：主机执行消息缺少有效存档，RequestId={message.RequestId}。");
            return;
        }

        _ = ExecuteLocalMultiplayerQuickSlAsync(message.RequestId, message.ConnectedPlayerIds, runSave);
    }

    private static void HandleQuickSlLoadReady(QuickSlLoadReadyMessage message, ulong senderId)
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Host)
        {
            return;
        }

        HostLoadBarrierState? barrierState = hostLoadBarrierState;
        if (barrierState == null || barrierState.RequestId != message.RequestId)
        {
            ModLogger.Warn($"多人快速 SL：收到过期或未知的载入就绪消息，RequestId={message.RequestId}，Sender={senderId}。");
            return;
        }

        barrierState.MarkReady(senderId);
    }

    private static void HandleQuickSlLoadBegin(QuickSlLoadBeginMessage message, ulong senderId)
    {
        if (!IsExecuteSenderValid(senderId))
        {
            return;
        }

        ClientLoadBarrierState? barrierState = clientLoadBarrierState;
        if (barrierState == null || barrierState.RequestId != message.RequestId)
        {
            ModLogger.Warn($"多人快速 SL：收到过期或未知的开始载入消息，RequestId={message.RequestId}，Sender={senderId}。");
            return;
        }

        barrierState.Begin();
    }

    private static void HandleQuickSlSetupReady(QuickSlSetupReadyMessage message, ulong senderId)
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Host)
        {
            return;
        }

        HostLoadBarrierState? barrierState = hostSetupBarrierState;
        if (barrierState == null || barrierState.RequestId != message.RequestId)
        {
            ModLogger.Warn($"多人快速 SL：收到过期或未知的重载同步器就绪消息，RequestId={message.RequestId}，Sender={senderId}。");
            return;
        }

        barrierState.MarkReady(senderId);
    }

    private static void HandleQuickSlRunBegin(QuickSlRunBeginMessage message, ulong senderId)
    {
        if (!IsExecuteSenderValid(senderId))
        {
            return;
        }

        ClientLoadBarrierState? barrierState = clientRunBeginBarrierState;
        if (barrierState == null || barrierState.RequestId != message.RequestId)
        {
            ModLogger.Warn($"多人快速 SL：收到过期或未知的开始重载消息，RequestId={message.RequestId}，Sender={senderId}。");
            return;
        }

        barrierState.Begin();
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

        if (clientLoadBarrierState?.RequestId == message.RequestId)
        {
            clientLoadBarrierState.Cancel();
            clientLoadBarrierState = null;
        }

        if (clientRunBeginBarrierState?.RequestId == message.RequestId)
        {
            clientRunBeginBarrierState.Cancel();
            clientRunBeginBarrierState = null;
        }

        if (clientInitiateState?.HostRequestId == message.RequestId ||
            clientInitiateState?.HostRequestId == null)
        {
            clientInitiateState = null;
        }

        ModLogger.Warn($"多人快速 SL：主机取消了本次 SL，原因={message.Reason}。");
    }

    private static async Task RespondToClientInitiateAsync(
        INetHostGameService hostService,
        HashSet<ulong> connectedPlayerIds,
        HostInitiateState initiateState)
    {
        bool approved = false;

        try
        {
            if (!QuickSlSettings.RequireMultiplayerHostConfirmation)
            {
                approved = true;
            }
            else if (JmcConfirmationPopup.IsAvailable)
            {
                approved = await JmcConfirmationPopup.ShowConfirmationAsync(
                    new LocString("settings_ui", "EXTENSION.QUICKSL.MULTIPLAYER_HOST_CONFIRM.title"),
                    new LocString("settings_ui", "EXTENSION.QUICKSL.MULTIPLAYER_HOST_CONFIRM.body"),
                    new LocString("settings_ui", "EXTENSION.QUICKSL.MULTIPLAYER_HOST_CONFIRM.confirm"),
                    new LocString("settings_ui", "EXTENSION.QUICKSL.MULTIPLAYER_HOST_CONFIRM.cancel"));
            }
            else
            {
                ModLogger.Warn("多人快速 SL：主机确认弹窗不可用，已拒绝客机发起请求。");
            }

            if (!ReferenceEquals(hostInitiateState, initiateState))
            {
                ModLogger.Info($"多人快速 SL：客机发起请求 {initiateState.ClientRequestId} 已失效，不再处理。");
                return;
            }

            if (!approved)
            {
                SendInitiateResponse(
                    hostService,
                    initiateState.InitiatorPlayerId,
                    initiateState.ClientRequestId,
                    initiateState.HostRequestId,
                    approved: false,
                    QuickSlCancelReason.Rejected);
                ModLogger.Warn($"多人快速 SL：主机拒绝了客机 {initiateState.InitiatorPlayerId} 的发起请求。");
                return;
            }

            SendInitiateResponse(
                hostService,
                initiateState.InitiatorPlayerId,
                initiateState.ClientRequestId,
                initiateState.HostRequestId,
                approved: true,
                QuickSlCancelReason.InvalidState);
            ModLogger.Info($"多人快速 SL：主机已同意客机 {initiateState.InitiatorPlayerId} 发起，RequestId={initiateState.HostRequestId}。");

            await RunApprovedHostQuickSlAsync(
                hostService,
                initiateState.HostRequestId,
                connectedPlayerIds,
                initiateState.InitiatorPlayerId);
        }
        catch (Exception ex)
        {
            ModLogger.Error("多人快速 SL：处理客机发起请求失败。", ex);
            if (ReferenceEquals(hostInitiateState, initiateState))
            {
                SendInitiateResponse(
                    hostService,
                    initiateState.InitiatorPlayerId,
                    initiateState.ClientRequestId,
                    initiateState.HostRequestId,
                    approved: false,
                    QuickSlCancelReason.InvalidState);
            }
        }
        finally
        {
            if (ReferenceEquals(hostInitiateState, initiateState))
            {
                hostInitiateState = null;
            }
        }
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

    private static bool TryPrepareHostQuickSl(
        [NotNullWhen(true)]
        out RunManager? runManager,
        [NotNullWhen(true)]
        out INetHostGameService? hostService,
        [NotNullWhen(true)]
        out HashSet<ulong>? connectedPlayerIds)
    {
        hostService = null;
        connectedPlayerIds = null;

        if (!TryGetValidatedMultiplayerContext(requireHost: true, out _, out runManager, out INetGameService? netService))
        {
            return false;
        }

        if (IsCombatStateSyncInProgress(runManager))
        {
            ModLogger.Warn("多人快速 SL 失败：当前多人状态同步尚未完成，请稍后再试。");
            return false;
        }

        if (netService is not INetHostGameService currentHostService)
        {
            ModLogger.Warn($"多人快速 SL 失败：当前不是主机模式，NetService={netService.Type}。");
            return false;
        }

        if (hostInitiateState != null)
        {
            ModLogger.Warn("多人快速 SL：已有客机发起请求正在等待主机处理。");
            return false;
        }

        if (hostVoteState != null)
        {
            ModLogger.Warn("多人快速 SL 已在等待客机确认，忽略重复触发。");
            return false;
        }

        if (hostLoadBarrierState != null)
        {
            ModLogger.Warn("多人快速 SL 已在等待客机载入准备，忽略重复触发。");
            return false;
        }

        hostService = currentHostService;
        connectedPlayerIds = GetConnectedRunPlayerIds(runManager, netService);
        return true;
    }

    private static async Task RunApprovedHostQuickSlAsync(
        INetHostGameService hostService,
        uint requestId,
        HashSet<ulong> connectedPlayerIds,
        ulong? initiatingClientId)
    {
        ulong[] remotePlayerIds = [.. connectedPlayerIds.Where(playerId => playerId != hostService.NetId)];

        if (remotePlayerIds.Length == 0)
        {
            SerializableRun? runSave = await LoadLocalMultiplayerRunSaveAsync(hostService);
            if (runSave == null)
            {
                return;
            }

            await ExecuteLocalMultiplayerQuickSlAsync(requestId, connectedPlayerIds, runSave);
            return;
        }

        ulong[] votePlayerIds =
        [
            .. remotePlayerIds.Where(playerId => initiatingClientId == null || playerId != initiatingClientId.Value)
        ];

        if (votePlayerIds.Length > 0)
        {
            using var timeoutCancel = new CancellationTokenSource();
            var voteState = new HostVoteState(requestId, connectedPlayerIds, votePlayerIds, timeoutCancel);
            hostVoteState = voteState;

            try
            {
                var requestMessage = new QuickSlRequestMessage
                {
                    RequestId = requestId,
                    RequiresClientConfirmation = QuickSlSettings.RequireMultiplayerClientConfirmation
                };

                foreach (ulong playerId in votePlayerIds)
                {
                    TrySendHostMessage(hostService, requestMessage, playerId);
                }

                _ = RunVoteTimeoutAsync(voteState);
                string requestMode = QuickSlSettings.RequireMultiplayerClientConfirmation ? "确认请求" : "静默准备请求";
                string initiatorNote = initiatingClientId == null ? string.Empty : "，已跳过发起客机";
                ModLogger.Info($"多人快速 SL：已向 {votePlayerIds.Length} 个客机发送{requestMode}{initiatorNote}，请等待所有人就绪。");

                bool approved = await voteState.Completion.Task;
                if (!approved)
                {
                    SendCancel(hostService, voteState);
                    ModLogger.Warn($"多人快速 SL 已取消，原因={voteState.CancelReason}。");
                    return;
                }
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
        else if (initiatingClientId != null)
        {
            ModLogger.Info("多人快速 SL：除发起客机外没有其他客机需要确认。");
        }

        SerializableRun? approvedRunSave = await LoadLocalMultiplayerRunSaveAsync(hostService);
        if (approvedRunSave == null)
        {
            SendCancel(hostService, requestId, QuickSlCancelReason.InvalidState, remotePlayerIds);
            return;
        }

        string? runSaveJson = TrySerializeRunSavePayload(approvedRunSave);
        if (runSaveJson == null)
        {
            SendCancel(hostService, requestId, QuickSlCancelReason.InvalidState, remotePlayerIds);
            return;
        }

        await ExecuteApprovedHostReloadAsync(
            hostService,
            requestId,
            remotePlayerIds,
            connectedPlayerIds,
            approvedRunSave,
            runSaveJson);
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

    private static async Task ExecuteApprovedHostReloadAsync(
        INetHostGameService hostService,
        uint requestId,
        IReadOnlyCollection<ulong> remotePlayerIds,
        IReadOnlyCollection<ulong> connectedPlayerIds,
        SerializableRun runSave,
        string runSaveJson)
    {
        HostLoadBarrierState? loadBarrierState = remotePlayerIds.Count > 0
            ? new HostLoadBarrierState(requestId, remotePlayerIds)
            : null;

        if (loadBarrierState != null)
        {
            hostLoadBarrierState = loadBarrierState;
        }

        try
        {
            SendExecute(hostService, requestId, remotePlayerIds, connectedPlayerIds, runSaveJson);
            ModLogger.Info($"多人快速 SL：已通知 {remotePlayerIds.Count} 个客机同步执行。");
            await ExecuteLocalMultiplayerQuickSlAsync(requestId, connectedPlayerIds, runSave);
        }
        finally
        {
            if (ReferenceEquals(hostLoadBarrierState, loadBarrierState))
            {
                hostLoadBarrierState = null;
            }
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

    private static async Task WaitForCoordinatedLoadBeginAsync(
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

    private static async Task WaitForClientsReadyToLoadAsync(
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

        HostLoadBarrierState barrierState = hostLoadBarrierState?.RequestId == requestId
            ? hostLoadBarrierState
            : new HostLoadBarrierState(requestId, remotePlayerIds);
        if (!ReferenceEquals(hostLoadBarrierState, barrierState))
        {
            hostLoadBarrierState = barrierState;
        }

        ModLogger.Debug($"多人快速 SL：等待 {remotePlayerIds.Length} 个客机完成载入准备，RequestId={requestId}。");
        await barrierState.WaitAsync(LoadBarrierTimeout);

        var beginMessage = new QuickSlLoadBeginMessage
        {
            RequestId = requestId
        };

        foreach (ulong playerId in remotePlayerIds)
        {
            TrySendHostMessage(hostService, beginMessage, playerId);
        }

        ModLogger.Debug($"多人快速 SL：所有客机已准备完毕，开始同步载入，RequestId={requestId}。");
    }

    private static async Task WaitForHostBeginLoadAsync(uint requestId, INetGameService netService)
    {
        var barrierState = new ClientLoadBarrierState(requestId);
        clientLoadBarrierState = barrierState;

        try
        {
            netService.SendMessage(new QuickSlLoadReadyMessage
            {
                RequestId = requestId
            });

            ModLogger.Debug($"多人快速 SL：已向主机报告载入准备完成，RequestId={requestId}。");
            await barrierState.WaitAsync(LoadBarrierTimeout);
        }
        finally
        {
            if (ReferenceEquals(clientLoadBarrierState, barrierState))
            {
                clientLoadBarrierState = null;
            }
        }
    }

    private static HostLoadBarrierState? PrepareHostSetupBarrier(
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
        hostSetupBarrierState = barrierState;
        return barrierState;
    }

    private static async Task WaitForCoordinatedRunBeginAsync(
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

    private static async Task WaitForClientsReadyToRunAsync(
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

        HostLoadBarrierState barrierState = hostSetupBarrierState?.RequestId == requestId
            ? hostSetupBarrierState
            : new HostLoadBarrierState(requestId, remotePlayerIds);
        if (!ReferenceEquals(hostSetupBarrierState, barrierState))
        {
            hostSetupBarrierState = barrierState;
        }

        ModLogger.Debug($"多人快速 SL：等待 {remotePlayerIds.Length} 个客机完成重载同步器注册，RequestId={requestId}。");
        await barrierState.WaitAsync(LoadBarrierTimeout);

        var beginMessage = new QuickSlRunBeginMessage
        {
            RequestId = requestId
        };

        foreach (ulong playerId in remotePlayerIds)
        {
            TrySendHostMessage(hostService, beginMessage, playerId);
        }

        ModLogger.Debug($"多人快速 SL：所有客机同步器已就绪，开始同步重载，RequestId={requestId}。");
    }

    private static async Task WaitForHostBeginRunAsync(uint requestId, INetGameService netService)
    {
        var barrierState = new ClientLoadBarrierState(requestId);
        clientRunBeginBarrierState = barrierState;

        try
        {
            netService.SendMessage(new QuickSlSetupReadyMessage
            {
                RequestId = requestId
            });

            ModLogger.Debug($"多人快速 SL：已向主机报告重载同步器就绪，RequestId={requestId}。");
            await barrierState.WaitAsync(LoadBarrierTimeout);
        }
        finally
        {
            if (ReferenceEquals(clientRunBeginBarrierState, barrierState))
            {
                clientRunBeginBarrierState = null;
            }
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
        HostLoadBarrierState? setupBarrierState = null;

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
            setupBarrierState = PrepareHostSetupBarrier(requestId, originalNetService, connectedPlayerIds);

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

            QuickSlSceneReloadGuard.PrepareCurrentHandForSceneSwap();
            DisposeNetworkPreservedRunSystems(runManager);

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

            await WaitForCoordinatedLoadBeginAsync(requestId, originalNetService, connectedPlayerIds);

            runManager.SetUpSavedMultiPlayer(runState, loadLobby);
            KeepOnlyConnectedPlayersInRunLobby(runManager, loadLobby.ConnectedPlayerIds);
            EnsureHandlersRegistered();

            await WaitForCoordinatedRunBeginAsync(requestId, originalNetService, connectedPlayerIds);

            using (QuickSlSceneReloadGuard.SuppressLateHandLayoutRefresh())
            {
                await game.LoadRun(runState, runSave.PreFinishedRoom);
            }

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
            if (ReferenceEquals(hostSetupBarrierState, setupBarrierState))
            {
                hostSetupBarrierState = null;
            }

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

    private static bool TryGetCurrentClientServiceForHost(
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

    private static bool IsCombatStateSyncInProgress(RunManager runManager)
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

    private static void DisposeNetworkPreservedRunSystems(RunManager runManager)
    {
        TryDisposeRunSystem("CombatStateSynchronizer", runManager.CombatStateSynchronizer);
        TryDisposeRunSystem("EventSynchronizer", runManager.EventSynchronizer);
        TryDisposeRunSystem("OneOffSynchronizer", runManager.OneOffSynchronizer);
        TryDisposeRunSystem("InputSynchronizer", runManager.InputSynchronizer);
    }

    private static void TryDisposeRunSystem(string name, IDisposable? disposable)
    {
        if (disposable == null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
            ModLogger.Debug($"多人快速 SL：已清理旧局网络同步器 {name}。");
        }
        catch (Exception ex)
        {
            ModLogger.Warn($"多人快速 SL：清理旧局网络同步器 {name} 时出现异常：{ex}");
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
            TrySendHostMessage(hostService, executeMessage, playerId);
        }
    }

    private static void SendCancel(INetHostGameService hostService, HostVoteState voteState)
    {
        SendCancel(
            hostService,
            voteState.RequestId,
            voteState.CancelReason,
            voteState.ConnectedPlayerIds.Where(playerId => playerId != hostService.NetId));
    }

    private static void SendCancel(
        INetHostGameService hostService,
        uint requestId,
        QuickSlCancelReason reason,
        IEnumerable<ulong> playerIds)
    {
        var cancelMessage = new QuickSlCancelMessage
        {
            RequestId = requestId,
            Reason = reason
        };

        foreach (ulong playerId in playerIds.Distinct())
        {
            TrySendHostMessage(hostService, cancelMessage, playerId);
        }
    }

    private static void SendInitiateResponse(
        INetHostGameService hostService,
        ulong playerId,
        uint clientRequestId,
        uint hostRequestId,
        bool approved,
        QuickSlCancelReason reason)
    {
        TrySendHostMessage(
            hostService,
            new QuickSlInitiateResponseMessage
            {
                ClientRequestId = clientRequestId,
                HostRequestId = hostRequestId,
                Approved = approved,
                Reason = reason
            },
            playerId);
    }

    private static void TrySendHostMessage<T>(INetHostGameService hostService, T message, ulong playerId)
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

    private sealed class HostInitiateState(uint clientRequestId, uint hostRequestId, ulong initiatorPlayerId)
    {
        public uint ClientRequestId { get; } = clientRequestId;

        public uint HostRequestId { get; } = hostRequestId;

        public ulong InitiatorPlayerId { get; } = initiatorPlayerId;
    }

    private sealed class ClientInitiateState(uint clientRequestId)
    {
        public uint ClientRequestId { get; } = clientRequestId;

        public uint? HostRequestId { get; set; }
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

    private sealed class HostLoadBarrierState
    {
        public HostLoadBarrierState(uint requestId, IEnumerable<ulong> expectedPlayers)
        {
            RequestId = requestId;
            ExpectedPlayers = [.. expectedPlayers];
        }

        public uint RequestId { get; }

        public HashSet<ulong> ExpectedPlayers { get; }

        public HashSet<ulong> ReadyPlayers { get; } = [];

        private TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void MarkReady(ulong playerId)
        {
            if (!ExpectedPlayers.Contains(playerId))
            {
                ModLogger.Warn($"多人快速 SL：收到非预期玩家 {playerId} 的载入就绪消息，已忽略。");
                return;
            }

            if (ReadyPlayers.Add(playerId))
            {
                ModLogger.Debug($"多人快速 SL：玩家 {playerId} 已完成载入准备，进度 {ReadyPlayers.Count}/{ExpectedPlayers.Count}。");
            }

            if (ReadyPlayers.Count >= ExpectedPlayers.Count)
            {
                Completion.TrySetResult(true);
            }
        }

        public async Task WaitAsync(TimeSpan timeout)
        {
            if (ReadyPlayers.Count >= ExpectedPlayers.Count)
            {
                return;
            }

            Task completedTask = await Task.WhenAny(Completion.Task, Task.Delay(timeout));
            if (!ReferenceEquals(completedTask, Completion.Task))
            {
                throw new TimeoutException($"等待客机载入准备超时，RequestId={RequestId}。");
            }

            if (!await Completion.Task)
            {
                throw new InvalidOperationException($"载入准备等待被取消，RequestId={RequestId}。");
            }
        }

        public void Cancel()
        {
            Completion.TrySetResult(false);
        }
    }

    private sealed class ClientLoadBarrierState(uint requestId)
    {
        public uint RequestId { get; } = requestId;

        private TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitAsync(TimeSpan timeout)
        {
            Task completedTask = await Task.WhenAny(Completion.Task, Task.Delay(timeout));
            if (!ReferenceEquals(completedTask, Completion.Task))
            {
                throw new TimeoutException($"等待主机开始载入超时，RequestId={RequestId}。");
            }

            if (!await Completion.Task)
            {
                throw new InvalidOperationException($"等待主机开始载入被取消，RequestId={RequestId}。");
            }
        }

        public void Begin()
        {
            Completion.TrySetResult(true);
        }

        public void Cancel()
        {
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
