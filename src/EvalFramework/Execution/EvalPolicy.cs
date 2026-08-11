namespace EvalFramework.Execution;

/// <summary>
/// Policy decisions that must not differ between callers.
/// </summary>
public static class EvalPolicy
{
    /// <summary>
    /// Whether candidate responses may be served from cache for a run of this many repetitions.
    /// </summary>
    /// <remarks>
    /// Caching is what makes a judge affordable on every pull request, but it is fatal to a
    /// reliability measurement: repeated identical prompts return one stored answer, so every
    /// repetition agrees, variance reads zero, and the confidence interval is computed over copies
    /// of a single observation. That does not merely add noise, it manufactures confidence that was
    /// never measured. Any run that repeats a case must talk to the model each time.
    /// </remarks>
    public static bool ShouldCacheCandidate(int repetitions) => repetitions <= 1;
}
