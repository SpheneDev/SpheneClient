namespace Sphene.Services;

public enum ChatEntryState
{
    /// <summary>Body traveled/stored as plaintext - pre-E2EE history. NOT
    /// cryptographically protected; the UI marks it accordingly.</summary>
    Plaintext,
    /// <summary>Successfully opened HPKE copy (native or migrated).</summary>
    Sealed,
    /// <summary>Ciphertext could not be opened locally (other device, rotated
    /// or missing key) - render a placeholder, never the blob.</summary>
    Undecryptable
}
