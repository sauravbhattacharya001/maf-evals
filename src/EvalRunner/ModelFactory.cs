using System.ClientModel;
using EvalFramework.Cost;
using Microsoft.Extensions.AI;
using OpenAI;

namespace EvalRunner;

/// <summary>
/// Builds chat clients from environment variables.
/// </summary>
/// <remarks>
/// The judge is configured separately so a different, usually stronger, model can grade the
/// candidate. Grading a model with itself correlates exactly the failure modes you want to detect.
/// Both clients are wrapped in a response cache: on a re-run with an unchanged prompt and model the
/// call is free, which is what makes a triad affordable on every pull request.
/// </remarks>
public static class ModelFactory
{
    public static (IChatClient Client, string Model, UsageTracker Usage) CreateCandidate(bool cache = true)
    {
        string key = EnvironmentSettings.Required(Lookup, "EVAL_API_KEY", "OPENAI_API_KEY");
        string model = EnvironmentSettings.Optional(Lookup, "gpt-4o-mini", "EVAL_MODEL");
        IChatClient client = Create(key, model, EnvironmentSettings.OptionalOrNull(Lookup, "EVAL_ENDPOINT"));
        UsageTracker usage = new(model);

        return (Wrap(client, cache, usage), model, usage);
    }

    public static (IChatClient Client, string Model, UsageTracker Usage) CreateJudge(bool cache = true)
    {
        string key = EnvironmentSettings.Required(Lookup, "JUDGE_API_KEY", "EVAL_API_KEY", "OPENAI_API_KEY");
        string model = EnvironmentSettings.Optional(Lookup, "gpt-4o", "JUDGE_MODEL");
        string? endpoint = EnvironmentSettings.OptionalOrNull(Lookup, "JUDGE_ENDPOINT", "EVAL_ENDPOINT");

        UsageTracker usage = new(model);

        return (Wrap(Create(key, model, endpoint), cache, usage), model, usage);
    }

    private static IChatClient Wrap(IChatClient client, bool cache, UsageTracker usage)
    {
        ChatClientBuilder builder = client.AsBuilder().UseFunctionInvocation();

        if (cache)
        {
            builder = builder.UseDistributedCache(new FileDistributedCache(RepoPaths.CacheDirectory));
        }

        // Added last so it sits below the cache: a cache hit costs nothing and must not be
        // reported as spend, otherwise the saving caching provides can never be verified.
        return builder.UseUsageTracking(usage).Build();
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

    private static Func<string, string?> Lookup => EnvironmentSettings.SystemLookup;
}

