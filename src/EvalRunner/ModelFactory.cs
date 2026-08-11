using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace EvalRunner;

/// <summary>
/// Builds chat clients from environment variables. The judge is configured separately so a
/// stronger or simply different model can grade the candidate.
/// </summary>
public static class ModelFactory
{
    public static (IChatClient Client, string Model) CreateCandidate()
    {
        string key = Required("EVAL_API_KEY", "OPENAI_API_KEY");
        string model = Environment.GetEnvironmentVariable("EVAL_MODEL") ?? "gpt-4o-mini";
        return (Create(key, model, Environment.GetEnvironmentVariable("EVAL_ENDPOINT")), model);
    }

    public static (IChatClient Client, string Model) CreateJudge()
    {
        string key = Environment.GetEnvironmentVariable("JUDGE_API_KEY")
            ?? Required("EVAL_API_KEY", "OPENAI_API_KEY");
        string model = Environment.GetEnvironmentVariable("JUDGE_MODEL") ?? "gpt-4o";
        string? endpoint = Environment.GetEnvironmentVariable("JUDGE_ENDPOINT")
            ?? Environment.GetEnvironmentVariable("EVAL_ENDPOINT");

        return (Create(key, model, endpoint), model);
    }

    private static IChatClient Create(string apiKey, string model, string? endpoint)
    {
        OpenAIClientOptions options = new();
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            options.Endpoint = new Uri(endpoint);
        }

        return new OpenAIClient(new ApiKeyCredential(apiKey), options)
            .GetChatClient(model)
            .AsIChatClient();
    }

    private static string Required(params string[] names)
    {
        foreach (string name in names)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        throw new InvalidOperationException(
            $"Set one of {string.Join(" or ", names)} to run model-backed tiers.");
    }
}
