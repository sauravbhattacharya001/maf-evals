using EvalFramework.Datasets;
using EvalFramework.Rules;

namespace EvalFramework.Tests;

/// <summary>
/// Measures whether the rules discriminate.
/// </summary>
/// <remarks>
/// The positive fixtures prove the rules do not fire on good answers. On their own that is weak
/// evidence: replacing every rule body with "passed" would keep them green. These tests require
/// each known-bad response to fail, and to fail for the stated reason.
/// </remarks>
public sealed class NegativeFixtureTests
{
    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "datasets")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("datasets not found.");
    }

    private static IReadOnlyList<GoldenCase> Cases() =>
        GoldenSet.Load(Path.Combine(RepoRoot(), "datasets", "support-golden-set.jsonl"));

    private static NegativeFixtureSet Fixtures() =>
        NegativeFixtureSet.Load(Path.Combine(RepoRoot(), "datasets", "negative-fixtures.json"));

    public static TheoryData<string, string> AllFixtures()
    {
        TheoryData<string, string> data = [];

        foreach (NegativeFixture fixture in Fixtures().Fixtures)
        {
            data.Add(fixture.CaseId, fixture.Label);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void EveryKnownBadResponseIsRejectedForTheStatedReason(string caseId, string label)
    {
        NegativeFixture fixture = Fixtures().Fixtures
            .Single(item => item.CaseId == caseId && item.Label == label);

        GoldenCase goldenCase = Cases().Single(item => item.Id == caseId);
        RuleReport report = ResponseRules.Evaluate(goldenCase.ToRuleSet(), fixture.Response);

        Assert.False(report.Passed, $"'{label}' should have failed case {caseId} but passed.");

        string[] actual = report.Failures.Select(failure => failure.Name).ToArray();

        foreach (string expected in fixture.ExpectedFailures)
        {
            Assert.True(
                actual.Contains(expected),
                $"'{label}' failed case {caseId}, but not via {expected}. Actual: {string.Join(", ", actual)}. " +
                "Catching a defect for the wrong reason means the rule is not the one protecting you.");
        }
    }

    [Fact]
    public void EveryCaseHasAtLeastOneNegativeFixture()
    {
        HashSet<string> covered = Fixtures().Fixtures
            .Select(fixture => fixture.CaseId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string[] uncovered = Cases()
            .Select(item => item.Id)
            .Where(id => !covered.Contains(id))
            .ToArray();

        Assert.True(
            uncovered.Length == 0,
            $"Cases with no known-bad example: {string.Join(", ", uncovered)}. " +
            "Such a case cannot demonstrate that its rules catch anything.");
    }

    [Fact]
    public void EveryFixtureNamesTheRuleItExercises()
    {
        Assert.All(Fixtures().Fixtures, fixture => Assert.NotEmpty(fixture.ExpectedFailures));
    }

    [Fact]
    public void FixturesReferenceRealCases()
    {
        HashSet<string> ids = Cases().Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.All(Fixtures().Fixtures, fixture => Assert.Contains(fixture.CaseId, ids));
    }

    [Fact]
    public void EveryRuleIsExercisedBySomeFixture()
    {
        // A rule no fixture triggers is untested in practice, however many positive cases exist.
        HashSet<string> exercised = Fixtures().Fixtures
            .SelectMany(fixture => fixture.ExpectedFailures)
            .ToHashSet(StringComparer.Ordinal);

        string[] expected =
        [
            RuleNames.MinLength,
            RuleNames.ExpectedTerms,
            RuleNames.ExpectedAnyTerms,
            RuleNames.ForbiddenTerms,
            RuleNames.ActionableFormat
        ];

        string[] missing = expected.Where(rule => !exercised.Contains(rule)).ToArray();

        Assert.True(missing.Length == 0, $"Rules no negative fixture exercises: {string.Join(", ", missing)}");
    }
}
