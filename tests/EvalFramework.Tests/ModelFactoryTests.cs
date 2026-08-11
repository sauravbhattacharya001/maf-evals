using EvalRunner;

namespace EvalFramework.Tests;

public sealed class ModelFactoryTests
{
    [Fact]
    public void BlankModelVariablesUseDefaults()
    {
        string? originalKey = Environment.GetEnvironmentVariable("EVAL_API_KEY");
        string? originalCandidate = Environment.GetEnvironmentVariable("EVAL_MODEL");
        string? originalJudge = Environment.GetEnvironmentVariable("JUDGE_MODEL");

        try
        {
            Environment.SetEnvironmentVariable("EVAL_API_KEY", "test-key");
            Environment.SetEnvironmentVariable("EVAL_MODEL", string.Empty);
            Environment.SetEnvironmentVariable("JUDGE_MODEL", " ");

            var (_, candidateModel, _) = ModelFactory.CreateCandidate(cache: false);
            var (_, judgeModel, _) = ModelFactory.CreateJudge(cache: false);

            Assert.Equal("gpt-4o-mini", candidateModel);
            Assert.Equal("gpt-4o", judgeModel);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EVAL_API_KEY", originalKey);
            Environment.SetEnvironmentVariable("EVAL_MODEL", originalCandidate);
            Environment.SetEnvironmentVariable("JUDGE_MODEL", originalJudge);
        }
    }
}
