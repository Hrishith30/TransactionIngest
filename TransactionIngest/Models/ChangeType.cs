namespace TransactionIngest.Models;

/// <summary>The type of change recorded in an audit log entry.</summary>
public enum ChangeType
{
    /// <summary>A new transaction was seen for the first time.</summary>
    Insert = 0,

    /// <summary>An existing transaction had one or more fields change.</summary>
    Update = 1,

    /// <summary>A transaction disappeared from the snapshot while still within the 24-hour window.</summary>
    Revoked = 2,

    /// <summary>A transaction aged out past 24 hours and was sealed permanently.</summary>
    Finalized = 3
}
