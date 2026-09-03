using LocalTransfer.Contracts.Transfers;

namespace LocalTransfer.Core.Transfers;

public sealed class TransferStateMachine
{
    private static readonly IReadOnlyDictionary<TransferState, HashSet<TransferState>> AllowedTransitions =
        new Dictionary<TransferState, HashSet<TransferState>>
        {
            [TransferState.WaitingForApproval] =
                [TransferState.Queued, TransferState.Rejected, TransferState.Canceled],
            [TransferState.Queued] =
                [TransferState.Transferring, TransferState.Paused, TransferState.WaitingForConnection,
                    TransferState.Failed, TransferState.Canceled],
            [TransferState.Transferring] =
                [TransferState.Paused, TransferState.WaitingForConnection, TransferState.Verifying,
                    TransferState.Failed, TransferState.Canceled],
            [TransferState.Paused] =
                [TransferState.Queued, TransferState.Transferring, TransferState.Failed, TransferState.Canceled],
            [TransferState.WaitingForConnection] =
                [TransferState.Queued, TransferState.Transferring, TransferState.Failed, TransferState.Canceled],
            [TransferState.Verifying] = [TransferState.Completed, TransferState.Failed],
            [TransferState.Failed] = [TransferState.Queued, TransferState.Canceled],
            [TransferState.Completed] = [],
            [TransferState.Rejected] = [],
            [TransferState.Canceled] = []
        };

    private readonly object _gate = new();
    private TransferState _state;

    public TransferStateMachine(TransferState initialState = TransferState.WaitingForApproval)
    {
        _state = initialState;
    }

    public event EventHandler<TransferState>? StateChanged;

    public TransferState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public bool CanTransitionTo(TransferState target)
    {
        lock (_gate)
        {
            return AllowedTransitions[_state].Contains(target);
        }
    }

    public void TransitionTo(TransferState target)
    {
        EventHandler<TransferState>? stateChanged;
        lock (_gate)
        {
            if (!AllowedTransitions[_state].Contains(target))
            {
                throw new InvalidOperationException($"Transfer cannot transition from {_state} to {target}.");
            }

            _state = target;
            stateChanged = StateChanged;
        }

        stateChanged?.Invoke(this, target);
    }
}
