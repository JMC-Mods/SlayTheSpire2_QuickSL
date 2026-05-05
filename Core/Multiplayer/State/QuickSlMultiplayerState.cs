using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace QuickSL.Core;

internal sealed class QuickSlMultiplayerState
{
    public uint NextRequestId { get; set; }

    public INetGameService? RegisteredNetService { get; set; }

    public HostInitiateState? HostInitiateState { get; set; }

    public HostVoteState? HostVoteState { get; set; }

    public HostLoadBarrierState? HostLoadBarrierState { get; set; }

    public HostLoadBarrierState? HostSetupBarrierState { get; set; }

    public ClientLoadBarrierState? ClientLoadBarrierState { get; set; }

    public ClientLoadBarrierState? ClientRunBeginBarrierState { get; set; }

    public ClientInitiateState? ClientInitiateState { get; set; }

    public uint? ActiveClientRequestId { get; set; }

    public void ResetForDisconnected()
    {
        HostInitiateState = null;
        HostVoteState?.Cancel(QuickSlCancelReason.InvalidState);
        HostVoteState = null;
        HostLoadBarrierState?.Cancel();
        HostLoadBarrierState = null;
        HostSetupBarrierState?.Cancel();
        HostSetupBarrierState = null;
        ClientLoadBarrierState?.Cancel();
        ClientLoadBarrierState = null;
        ClientRunBeginBarrierState?.Cancel();
        ClientRunBeginBarrierState = null;
        ClientInitiateState = null;
        ActiveClientRequestId = null;
    }
}
