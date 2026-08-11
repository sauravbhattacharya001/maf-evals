using EvalFramework.Statistics;

namespace EvalFramework.Tests;

/// <summary>
/// Validates the confidence interval by simulation rather than by example.
/// </summary>
/// <remarks>
/// The existing tests assert hand-picked values, which only checks that a formula was transcribed
/// as intended. The claim actually being made is about coverage: a 95% interval should contain the
/// true rate about 95% of the time. That is testable directly by sampling from a known rate, and it
/// is the property Tier 3 relies on when it gates on a lower bound. A fixed seed keeps it
/// deterministic, so this is a real assertion rather than an occasional flake.
/// </remarks>
public sealed class WilsonCoverageTests
{
    private const int Trials = 2000;

    private static int Successes(Random random, int n, double p)
    {
        int count = 0;

        for (int i = 0; i < n; i++)
        {
            if (random.NextDouble() < p)
            {
                count++;
            }
        }

        return count;
    }

    private static double Coverage(double p, int n, int seed)
    {
        Random random = new(seed);
        int covered = 0;

        for (int trial = 0; trial < Trials; trial++)
        {
            ConfidenceInterval interval = Wilson.Interval(Successes(random, n, p), n);

            if (p >= interval.Lower && p <= interval.Upper)
            {
                covered++;
            }
        }

        return (double)covered / Trials;
    }

    [Theory]
    [InlineData(0.50, 20)]
    [InlineData(0.80, 20)]
    [InlineData(0.95, 40)]
    [InlineData(0.70, 10)]
    public void IntervalCoversTheTrueRateAboutNinetyFivePercentOfTheTime(double p, int n)
    {
        double coverage = Coverage(p, n, seed: 20260811);

        // Wilson is slightly conservative for small samples, so allow 92% to 100%.
        Assert.InRange(coverage, 0.92, 1.0);
    }

    [Fact]
    public void CoverageDipsNearTheUpperBoundaryWhichIsAKnownWilsonProperty()
    {
        // Measured 91.4% at p=0.98, n=25 against a nominal 95%. Wilson coverage oscillates with
        // the discreteness of the binomial and dips near p close to 1. This is not a defect in the
        // implementation, but it matters here: a healthy agent lives in exactly this region, so the
        // Tier 3 lower-bound gate is slightly anti-conservative where it is used most. Documented
        // in the README limitations rather than hidden behind a loosened assertion.
        double coverage = Coverage(0.98, 25, seed: 7);

        Assert.InRange(coverage, 0.88, 1.0);
        Assert.True(coverage < 0.95, "If this rises to nominal, the note in the README is stale.");
    }

    [Fact]
    public void IntervalIsNeverEmptyForAPerfectSample()
    {
        // 5 of 5 must not imply certainty; that false confidence is why Wilson was chosen.
        ConfidenceInterval interval = Wilson.Interval(5, 5);

        Assert.True(interval.Width > 0.3);
    }

    [Fact]
    public void WidthShrinksMonotonicallyWithSampleSize()
    {
        double previous = double.MaxValue;

        foreach (int n in new[] { 5, 10, 20, 50, 100, 500 })
        {
            double width = Wilson.Interval((int)Math.Round(n * 0.8), n).Width;

            Assert.True(width < previous, $"width did not shrink at n={n}");
            previous = width;
        }
    }

    [Fact]
    public void PointEstimateAlwaysLiesInsideTheInterval()
    {
        // Tolerance covers floating-point rounding at k = n, where the upper bound is 1 in exact
        // arithmetic but can land a fraction below it.
        const double epsilon = 1e-9;

        for (int n = 1; n <= 50; n++)
        {
            for (int k = 0; k <= n; k++)
            {
                ConfidenceInterval interval = Wilson.Interval(k, n);
                double observed = (double)k / n;

                Assert.InRange(observed, interval.Lower - epsilon, interval.Upper + epsilon);
            }
        }
    }

    [Fact]
    public void AWiderConfidenceLevelProducesAWiderInterval()
    {
        double ninetyFive = Wilson.Interval(8, 10).Width;
        double ninetyNine = Wilson.Interval(8, 10, z: 2.575829).Width;

        Assert.True(ninetyNine > ninetyFive);
    }
}
