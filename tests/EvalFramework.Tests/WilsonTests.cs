using EvalFramework.Statistics;

namespace EvalFramework.Tests;

public sealed class WilsonTests
{
    [Fact]
    public void PerfectSmallSampleStillCarriesRealUncertainty()
    {
        // The naive interval would report 100% to 100% here, which is the exact
        // false confidence Tier 2 exists to prevent.
        ConfidenceInterval interval = Wilson.Interval(5, 5);

        Assert.True(interval.Lower < 0.60);
        Assert.Equal(1.0, interval.Upper, 6);
    }

    [Fact]
    public void MoreTrialsNarrowTheInterval()
    {
        ConfidenceInterval few = Wilson.Interval(9, 10);
        ConfidenceInterval many = Wilson.Interval(90, 100);

        Assert.True(many.Width < few.Width);
    }

    [Fact]
    public void BoundsStayWithinZeroAndOne()
    {
        ConfidenceInterval none = Wilson.Interval(0, 7);

        Assert.Equal(0.0, none.Lower, 6);
        Assert.InRange(none.Upper, 0.0, 1.0);
    }

    [Fact]
    public void ZeroTrialsIsFullyUncertainRatherThanAFailure()
    {
        ConfidenceInterval interval = Wilson.Interval(0, 0);

        Assert.Equal(0.0, interval.Lower, 6);
        Assert.Equal(1.0, interval.Upper, 6);
    }

    [Fact]
    public void MoreSuccessesThanTrialsIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Wilson.Interval(6, 5));
    }
}
