namespace LocalTransfer.Contracts.Transfers;

public enum TransferState
{
    WaitingForApproval,
    Queued,
    Transferring,
    Paused,
    WaitingForConnection,
    Verifying,
    Completed,
    Failed,
    Rejected,
    Canceled
}
