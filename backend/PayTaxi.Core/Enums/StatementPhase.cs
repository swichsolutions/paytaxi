namespace PayTaxi.Core.Enums;

/// <summary>Where a licensee's monthly statement stands against the barrier of licence agreement §2.3.</summary>
public enum StatementPhase
{
    /// <summary>The barrier is not filled even after this month: Swich takes fee income − licensee-paid costs.</summary>
    Barrier,

    /// <summary>This month fills the barrier: the part up to it at the barrier rule, the rest split normally.</summary>
    Crossing,

    /// <summary>The barrier was filled earlier: the §2.2/§2.4 split applies to the whole month.</summary>
    Normal,
}

/// <summary>A licensee's monthly statement, as the sum of its per-park transfers.</summary>
public enum MonthlyStatementStatus
{
    /// <summary>Computed; one or more park transfers not yet confirmed.</summary>
    Pending,

    /// <summary>Every park transfer confirmed (or there was nothing to collect).</summary>
    Completed,

    /// <summary>At least one park transfer failed; retried every night until it succeeds.</summary>
    Failed,
}
