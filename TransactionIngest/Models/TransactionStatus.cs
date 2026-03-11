namespace TransactionIngest.Models;

/// <summary>Lifecycle state of a transaction in our local store.</summary>
public enum TransactionStatus
{
    /// <summary>Transaction was seen in the latest snapshot — no issues.</summary>
    Active = 0,

    /// <summary>
    /// Transaction was within the 24-hour window but is no longer in the snapshot.
    /// Could be a cancellation or upstream correction.
    /// </summary>
    Revoked = 1,

    /// <summary>
    /// Transaction is older than 24 hours and has been sealed.
    /// Finalized records are never modified.
    /// </summary>
    Finalized = 2
}
