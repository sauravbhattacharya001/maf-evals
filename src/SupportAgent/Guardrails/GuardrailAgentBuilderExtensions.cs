using Microsoft.Agents.AI;

namespace SupportAgent.Guardrails;

/// <summary>Wires the Tier 1 guards into a Microsoft Agent Framework pipeline.</summary>
public static class GuardrailAgentBuilderExtensions
{
    /// <summary>
    /// Adds final-response validation with corrective retries.
    /// </summary>
    /// <remarks>Add this before <see cref="UseToolGuard"/> so it wraps the tool guard.</remarks>
    public static AIAgentBuilder UseResponseGuard(this AIAgentBuilder builder, ResponseGuard guard)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(guard);

        return builder.Use(
            runFunc: (messages, session, options, innerAgent, cancellationToken) =>
                guard.RunAsync(
                    messages,
                    session,
                    options,
                    (m, s, o, ct) => innerAgent.RunAsync(m, s, o, ct),
                    cancellationToken),
            runStreamingFunc: null);
    }

    /// <summary>
    /// Adds tool-argument validation. Requires a chat-client-backed agent, since the underlying
    /// hook lives in <c>FunctionInvokingChatClient</c>.
    /// </summary>
    public static AIAgentBuilder UseToolGuard(this AIAgentBuilder builder, ToolGuard guard)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(guard);

        return builder.Use(guard.InvokeAsync);
    }
}
