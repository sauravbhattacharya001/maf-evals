namespace EvalFramework.Statistics;

/// <summary>
/// Wilson score interval. Preferred over the normal approximation because golden sets
/// are small and observed pass rates are often at or near 1.0, where the naive interval
/// collapses to zero width and hides real uncertainty.
/// </summary>
public readonly record struct ConfidenceInterval(double Lower, double Upper)
{
    public double Width => Upper - Lower;
}

public static class Wilson
{
    /// <summary>1.959964 corresponds to a two-sided 95% interval.</summary>
    public const double Z95 = 1.959964;

    public static ConfidenceInterval Interval(int successes, int trials, double z = Z95)
    {
        if (trials <= 0)
        {
            return new ConfidenceInterval(0d, 1d);
        }

        if (successes < 0 || successes > trials)
        {
            throw new ArgumentOutOfRangeException(nameof(successes), "successes must be within [0, trials].");
        }

        double n = trials;
        double phat = successes / n;
        double z2 = z * z;
        double denominator = 1d + (z2 / n);
        double center = phat + (z2 / (2d * n));
        double margin = z * Math.Sqrt((phat * (1d - phat) / n) + (z2 / (4d * n * n)));

        double lower = (center - margin) / denominator;
        double upper = (center + margin) / denominator;

        return new ConfidenceInterval(Math.Clamp(lower, 0d, 1d), Math.Clamp(upper, 0d, 1d));
    }
}
