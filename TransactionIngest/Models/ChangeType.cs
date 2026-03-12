namespace TransactionIngest.Models;

/// <summary>Types of changes we track in the audit log.</summary>
public enum ChangeType
{
    /// <summary>New transaction added.</summary>
    Insert = 0,

    /// <summary>Updated an existing transaction's fields.</summary>
    Update = 1,

    /// <summary>Transaction was revoked from the feed.</summary>
    Revoked = 2,

    /// <summary>Transaction aged out and was finalized.</summary>
    Finalized = 3
}
