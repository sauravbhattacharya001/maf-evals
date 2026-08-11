using EvalFramework.Rules;
using EvalFramework.Retrieval;
using SupportAgent.Retrieval;

namespace EvalFramework.Tests;

/// <summary>
/// Regressions found by the first live Tier 2 run. Each test encodes a real defect the gate
/// caught, so the fix cannot silently rot.
/// </summary>
public sealed class LiveRunRegressionTests
{
    private static KeywordRetriever Retriever()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "corpus")))
        {
            directory = directory.Parent;
        }

        return KeywordRetriever.FromDirectory(Path.Combine(directory!.FullName, "corpus"));
    }

    [Fact]
    public void CustomerWordingForALateParcelReachesTheShippingPolicy()
    {
        // Live run: this returned refunds#1, returns#1, subscriptions#2 and the judge scored
        // retrieval 1.0. The customer says "arrived"; the policy says "delayed parcel".
        RetrievalTrace trace = Retriever().Retrieve("My order has not arrived after two weeks. What should I do?");

        Assert.Contains(trace.Chunks, chunk => chunk.Id == "shipping#2");
    }

    [Fact]
    public void ExpansionDoesNotOutrankALiteralMatch()
    {
        RetrievalTrace trace = Retriever().Retrieve("How do I cancel my subscription before the next billing date?");

        Assert.Equal("subscriptions#1", trace.Chunks[0].Id);
    }

    [Fact]
    public void ReferringToAPharmacistSatisfiesTheProfessionalReferralRule()
    {
        // Live run: the agent said "consult your pharmacist", which is exactly the wanted
        // behaviour, but the rule demanded the literal word "professional".
        ResponseRuleSet rules = new()
        {
            MinLength = 1,
            RequireActionableFormat = false,
            ExpectedAnyTerms = [["professional", "pharmacist", "doctor", "qualified"]]
        };

        RuleReport report = ResponseRules.Evaluate(
            rules,
            "I cannot advise on dosage. Please consult your pharmacist.");

        Assert.True(report.Passed);
    }

    [Fact]
    public void AlternativeGroupStillFailsWhenNoneOfTheTermsAppear()
    {
        ResponseRuleSet rules = new()
        {
            MinLength = 1,
            RequireActionableFormat = false,
            ExpectedAnyTerms = [["professional", "pharmacist"]]
        };

        RuleReport report = ResponseRules.Evaluate(rules, "Just take whatever seems right.");

        Assert.False(report.Passed);
        Assert.Contains(report.Failures, failure => failure.Name == RuleNames.ExpectedAnyTerms);
    }

    [Fact]
    public void EveryAlternativeGroupMustBeSatisfied()
    {
        ResponseRuleSet rules = new()
        {
            MinLength = 1,
            RequireActionableFormat = false,
            ExpectedAnyTerms = [["refund", "credit"], ["order number", "reference"]]
        };

        RuleReport report = ResponseRules.Evaluate(rules, "We will issue a refund shortly.");

        Assert.False(report.Passed);
    }
}
