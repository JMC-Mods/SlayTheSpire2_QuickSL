using JmcModLib.Utils;

namespace QuickSL.Core;

internal sealed class HostInitiateState(uint clientRequestId, uint hostRequestId, ulong initiatorPlayerId)
{
    public uint ClientRequestId { get; } = clientRequestId;

    public uint HostRequestId { get; } = hostRequestId;

    public ulong InitiatorPlayerId { get; } = initiatorPlayerId;
}

internal sealed class ClientInitiateState(uint clientRequestId)
{
    public uint ClientRequestId { get; } = clientRequestId;

    public uint? HostRequestId { get; set; }
}

internal sealed class HostVoteState
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

    public ulong RelatedPlayerId { get; private set; }

    public CancellationTokenSource TimeoutCancel { get; }

    public TaskCompletionSource<bool> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Cancel(QuickSlCancelReason reason, ulong relatedPlayerId = 0)
    {
        CancelReason = reason;
        RelatedPlayerId = relatedPlayerId;
        Completion.TrySetResult(false);
    }
}

internal sealed class HostLoadBarrierState
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

internal sealed class ClientLoadBarrierState(uint requestId)
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
