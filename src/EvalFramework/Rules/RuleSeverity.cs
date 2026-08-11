namespace EvalFramework.Rules;

/// <summary>
/// What Tier 1 should do at runtime when a rule fails.
/// </summary>
/// <remarks>
/// Only Tier 1 honours severity. Tier 2 treats every rule as blocking, because a
/// warn-level rule failing across the golden set is still a regression worth catching
/// before merge, even if it is tolerable for a single production response.
/// </remarks>
public enum RuleSeverity
{
    /// <summary>Record the failure and let the response through.</summary>
    Warn = 0,

    /// <summary>Retry the request; degrade to <see cref="Warn"/> once attempts are exhausted.</summary>
    Retry = 1,

    /// <summary>Retry, then fail closed. The response must never reach the user.</summary>
    Block = 2
}
