using Microsoft.Extensions.AI;

namespace EvalFramework.Cost;

/// <summary>
/// Records token usage for every call that reaches the provider.
/// </summary>
/// <remarks>
/// Install this as the innermost stage of the chat pipeline, below any cache. Placed above a cache
/// it would report the usage stored in the cached response and count spend that never happened.
/// </remarks>
public sealed class UsageTrackingChatClient(IChatClient innerClient, UsageTracker tracker)
    : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response = await base
            .GetResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false);

        tracker.Record(response.Usage);

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ChatResponseUpdate update in base
            .GetStreamingResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false))
        {
            // Streaming reports usage as a content item, usually on the final update.
            foreach (AIContent content in update.Contents)
            {
                if (content is UsageContent usage)
                {
                    tracker.Record(usage.Details);
                }
            }

            yield return update;
        }
    }
}

public static class UsageTrackingChatClientExtensions
{
    /// <summary>Adds usage tracking. Call last so it sits below the cache.</summary>
    public static ChatClientBuilder UseUsageTracking(this ChatClientBuilder builder, UsageTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tracker);

        return builder.Use(inner => new UsageTrackingChatClient(inner, tracker));
    }
}
