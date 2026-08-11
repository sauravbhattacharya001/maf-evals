using EvalFramework.Retrieval;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SupportAgent.Guardrails;
using SupportAgent.Retrieval;

namespace SupportAgent;

/// <summary>
/// Retrieves policy context for the incoming request and injects it as a system message.
/// </summary>
/// <remarks>
/// Placed outermost in the pipeline so retrieval happens once per request. If it ran inside the
/// response guard, every corrective retry would re-retrieve and the captured trace would no longer
/// describe the context that produced the final answer.
/// </remarks>
public sealed class RetrievalAugmenter(
    IRetriever retriever,
    int topK = 3,
    Action<RetrievalTrace>? onRetrieved = null)
{
    public async Task<AgentResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AgentContinuation next,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(next);

        List<ChatMessage> list = messages.ToList();
        string query = list.LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty;

        RetrievalTrace trace = string.IsNullOrWhiteSpace(query)
            ? RetrievalTrace.Empty(query)
            : retriever.Retrieve(query, topK);

        onRetrieved?.Invoke(trace);

        if (trace.Chunks.Count > 0)
        {
            list.Insert(0, new ChatMessage(
                ChatRole.System,
                "Answer using only the following policy extracts. If they do not cover the question, "
                + "say so rather than guessing.\n\n" + trace.Combined));
        }

        return await next(list, session, options, cancellationToken).ConfigureAwait(false);
    }
}

