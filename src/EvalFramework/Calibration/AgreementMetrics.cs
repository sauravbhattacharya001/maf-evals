using System.Text.Json.Serialization;
using EvalFramework.RagTriad;

namespace EvalFramework.Calibration;

/// <summary>One judge score placed next to the human score for the same case.</summary>
public sealed record ScorePair(
    [property: JsonPropertyName("caseId")] string CaseId,
    [property: JsonPropertyName("metric")] string Metric,
    [property: JsonPropertyName("human")] double Human,
    [property: JsonPropertyName("judge")] double? Judge,
    [property: JsonPropertyName("repetition")] int Repetition = 1);

/// <summary>Agreement between judge and human for a single metric.</summary>
public sealed record MetricAgreement
{
    [JsonPropertyName("metric")]
    public required string Metric { get; init; }

    [JsonPropertyName("compared")]
    public required int Compared { get; init; }

    /// <summary>Fraction scored identically.</summary>
    [JsonPropertyName("exactAgreement")]
    public required double ExactAgreement { get; init; }

    /// <summary>Fraction within one point. The usable bar for a 1 to 5 scale.</summary>
    [JsonPropertyName("withinOne")]
    public required double WithinOne { get; init; }

    [JsonPropertyName("meanAbsoluteError")]
    public required double MeanAbsoluteError { get; init; }

    /// <summary>Signed mean. Positive means the judge scores higher than the human.</summary>
    [JsonPropertyName("bias")]
    public required double Bias { get; init; }

    [JsonPropertyName("correlation")]
    public required double Correlation { get; init; }

    /// <summary>
    /// Fraction where both land in the same band. This is what actually gates a merge.
    /// </summary>
    [JsonPropertyName("bandAgreement")]
    public required double BandAgreement { get; init; }

    /// <summary>Cases where one side would block and the other would not.</summary>
    [JsonPropertyName("disagreements")]
    public IReadOnlyList<string> Disagreements { get; init; } = [];
}

/// <summary>
/// Compares judge scores with human labels.
/// </summary>
/// <remarks>
/// Correlation alone is not enough: a judge that scores everything two points low correlates
/// perfectly and still fails every merge. Bias and band agreement are reported because the gate
/// depends on which side of a threshold a score falls, not on how well the two rankings align.
/// </remarks>
public static class AgreementMetrics
{
    public static MetricAgreement Compare(
        string metric,
        IReadOnlyList<ScorePair> pairs,
        ThresholdBand band)
    {
        ScorePair[] scored = pairs
            .Where(pair => pair.Metric == metric && pair.Judge.HasValue)
            .ToArray();

        if (scored.Length == 0)
        {
            return new MetricAgreement
            {
                Metric = metric,
                Compared = 0,
                ExactAgreement = 0,
                WithinOne = 0,
                MeanAbsoluteError = 0,
                Bias = 0,
                Correlation = 0,
                BandAgreement = 0
            };
        }

        double[] human = scored.Select(pair => pair.Human).ToArray();
        double[] judge = scored.Select(pair => pair.Judge!.Value).ToArray();

        List<string> disagreements = [];

        foreach (ScorePair pair in scored)
        {
            TriadVerdict humanVerdict = band.Classify(pair.Human);
            TriadVerdict judgeVerdict = band.Classify(pair.Judge);

            bool humanBlocks = humanVerdict == TriadVerdict.Fail;
            bool judgeBlocks = judgeVerdict == TriadVerdict.Fail;

            if (humanBlocks != judgeBlocks)
            {
                disagreements.Add(
                    $"{pair.CaseId}: human {pair.Human:F1} ({humanVerdict}), " +
                    $"judge {pair.Judge:F1} ({judgeVerdict})");
            }
        }

        return new MetricAgreement
        {
            Metric = metric,
            Compared = scored.Length,
            ExactAgreement = scored.Count(p => Math.Abs(p.Human - p.Judge!.Value) < 0.5) / (double)scored.Length,
            WithinOne = scored.Count(p => Math.Abs(p.Human - p.Judge!.Value) <= 1.0) / (double)scored.Length,
            MeanAbsoluteError = scored.Average(p => Math.Abs(p.Human - p.Judge!.Value)),
            Bias = scored.Average(p => p.Judge!.Value - p.Human),
            Correlation = Pearson(human, judge),
            BandAgreement =
                scored.Count(p => band.Classify(p.Human) == band.Classify(p.Judge)) / (double)scored.Length,
            Disagreements = disagreements
        };
    }

    /// <summary>Returns 0 when either side is constant, since correlation is undefined there.</summary>
    internal static double Pearson(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        if (left.Count != right.Count || left.Count < 2)
        {
            return 0;
        }

        double leftMean = left.Average();
        double rightMean = right.Average();

        double covariance = 0;
        double leftVariance = 0;
        double rightVariance = 0;

        for (int i = 0; i < left.Count; i++)
        {
            double dl = left[i] - leftMean;
            double dr = right[i] - rightMean;

            covariance += dl * dr;
            leftVariance += dl * dl;
            rightVariance += dr * dr;
        }

        double denominator = Math.Sqrt(leftVariance * rightVariance);

        return denominator == 0 ? 0 : covariance / denominator;
    }
}

