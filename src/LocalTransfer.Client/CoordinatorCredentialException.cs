namespace LocalTransfer.Client;

/// <summary>
/// The coordinator rejected the stored pairing credential with HTTP 401.
/// <para>
/// This means the device is no longer trusted by the desktop: the user removed it from the
/// device list, or a newer pairing replaced the credential while this client kept the old one.
/// Retrying cannot help — the caller must discard the persisted connection and pair again, so
/// the raw <c>401 (Unauthorized)</c> the runtime would surface is replaced with this type.
/// </para>
/// </summary>
public sealed class CoordinatorCredentialException : Exception
{
    public CoordinatorCredentialException()
        : base("The coordinator no longer accepts this device's pairing credential.")
    {
    }

    public CoordinatorCredentialException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
