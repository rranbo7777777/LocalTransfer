using LocalTransfer.Contracts.Transfers;
using LocalTransfer.Core.Transfers;

namespace LocalTransfer.Core.Tests;

public sealed class TransferStateMachineTests
{
    [Fact]
    public void HappyPath_ReachesCompleted()
    {
        var machine = new TransferStateMachine();

        machine.TransitionTo(TransferState.Queued);
        machine.TransitionTo(TransferState.Transferring);
        machine.TransitionTo(TransferState.Verifying);
        machine.TransitionTo(TransferState.Completed);

        Assert.Equal(TransferState.Completed, machine.State);
    }

    [Fact]
    public void TerminalState_RejectsFurtherTransitions()
    {
        var machine = new TransferStateMachine();
        machine.TransitionTo(TransferState.Rejected);

        var exception = Assert.Throws<InvalidOperationException>(
            () => machine.TransitionTo(TransferState.Queued));

        Assert.Contains("Rejected", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedTransfer_CanBeQueuedForRetry()
    {
        var machine = new TransferStateMachine(TransferState.Failed);

        machine.TransitionTo(TransferState.Queued);

        Assert.Equal(TransferState.Queued, machine.State);
    }
}
