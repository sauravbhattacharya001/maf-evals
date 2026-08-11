using EvalFramework.Execution;

namespace EvalFramework.Tests;

/// <summary>
/// Caching and reliability measurement are in direct conflict, and the conflict is silent: a cached
/// run produces confident numbers rather than obviously broken ones.
/// </summary>
public sealed class EvalPolicyTests
{
    [Fact]
    public void SinglePassRunsMayUseTheCache()
    {
        // Tier 2 gates a pull request once per case, and caching is what keeps a judge affordable.
        Assert.True(EvalPolicy.ShouldCacheCandidate(1));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(50)]
    public void RepeatedRunsMustBypassTheCache(int repetitions)
    {
        // Otherwise every repetition returns one stored answer: variance reads zero and the
        // confidence interval describes the cache instead of the agent.
        Assert.False(EvalPolicy.ShouldCacheCandidate(repetitions));
    }

    [Fact]
    public void ZeroOrNegativeRepetitionsDoNotDisableCachingAccidentally()
    {
        // Guards the boundary: the runner rejects these separately, and this must not be the
        // place a nonsensical value silently changes behaviour.
        Assert.True(EvalPolicy.ShouldCacheCandidate(0));
    }
}
