namespace TransactionIngest.Models;

/// <summary>Status of a transaction in our local store.</summary>
public enum TransactionStatus
{
    /// <summary>Active and present in the latest sync.</summary>
    Active = 0,

    /// <summary>Revoked because it's missing from the feed but still in the window.</summary>
    Revoked = 1,

    /// <summary>Older than 24 hours and locked from further changes.</summary>
    Finalized = 2
}
