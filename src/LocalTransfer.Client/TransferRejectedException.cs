namespace LocalTransfer.Client;

/// <summary>
/// The desktop declined an offered transfer, or an approval prompt nobody answered expired.
/// <para>
/// This is deliberately not an <see cref="UnauthorizedAccessException"/>: pairing is intact and
/// the credential is fine, the file simply was not accepted. Keeping it separate lets the phone
/// say "the computer did not accept this send" instead of showing a technical auth message.
/// </para>
/// </summary>
public sealed class TransferRejectedException : Exception
{
    public TransferRejectedException()
        : base("The computer did not accept the file transfer.")
    {
    }

    public TransferRejectedException(string message)
        : base(message)
    {
    }

    public TransferRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
