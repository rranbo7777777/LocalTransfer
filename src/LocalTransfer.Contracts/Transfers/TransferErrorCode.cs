namespace LocalTransfer.Contracts.Transfers;

public enum TransferErrorCode
{
    None,
    InvalidRequest,
    UnauthorizedDevice,
    UnsupportedProtocol,
    FileNotFound,
    InsufficientStorage,
    ChunkHashMismatch,
    FileHashMismatch,
    ConnectionLost,
    Canceled,
    InternalError
}
