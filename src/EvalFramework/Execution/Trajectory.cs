using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace EvalFramework.Execution;

/// <summary>A tool call as it appeared in the conversation, with the result it produced.</summary>
public sealed record TrajectoryToolCall(
    [property: JsonPropertyName("callId")] string CallId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] IReadOnlyDictionary<string, object?> Arguments,
    [property: JsonPropertyName("result")] string? Result = null);

/// <summary>One turn in the conversation the agent actually had.</summary>
public sealed record TrajectoryMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("toolCalls")]
    public IReadOnlyList<TrajectoryToolCall> ToolCalls { get; init; } = [];
}

/// <summary>
/// The full reasoning trajectory of one run: every turn, tool call, and tool result.
/// </summary>
/// <remarks>
/// <para>
/// The final answer alone cannot show whether an agent reasoned soundly. An agent that guesses the
/// right answer without checking, calls a tool it did not need, or ignores what a tool told it,
/// produces text indistinguishable from one that worked properly. Trajectory evaluation asks about
/// the path, so the path has to be recorded.
/// </para>
/// <para>
/// Stored in the artifact rather than held in memory, so a trajectory can be re-judged later without
/// re-running the agent, exactly as Tier 2 re-uses recorded responses.
/// </para>
/// </remarks>
public static class Trajectory
{
    public static IReadOnlyList<TrajectoryMessage> Capture(IEnumerable<ChatMessage> messages)
    {
        List<TrajectoryMessage> captured = [];

        // Tool results arrive on later messages, so calls are matched to results by id afterwards.
        Dictionary<string, string> results = new(StringComparer.Ordinal);

        foreach (ChatMessage message in messages)
        {
            foreach (AIContent content in message.Contents)
            {
                if (content is FunctionResultContent result)
                {
                    results[result.CallId] = result.Result?.ToString() ?? string.Empty;
                }
            }
        }

        foreach (ChatMessage message in messages)
        {
            TrajectoryToolCall[] calls = message.Contents
                .OfType<FunctionCallContent>()
                .Select(call => new TrajectoryToolCall(
                    call.CallId,
                    call.Name,
                    call.Arguments?.ToDictionary(pair => pair.Key, pair => pair.Value)
                        ?? new Dictionary<string, object?>(),
                    results.GetValueOrDefault(call.CallId)))
                .ToArray();

            // A message that carries only a tool result adds nothing a reader can use; the result
            // is already attached to the call that produced it.
            if (calls.Length == 0 && string.IsNullOrWhiteSpace(message.Text))
            {
                continue;
            }

            captured.Add(new TrajectoryMessage
            {
                Role = message.Role.Value,
                Text = message.Text ?? string.Empty,
                ToolCalls = calls
            });
        }

        return captured;
    }

    /// <summary>Rebuilds chat messages so a saved trajectory can be judged without a fresh run.</summary>
    public static IReadOnlyList<ChatMessage> ToChatMessages(
        string query,
        IReadOnlyList<TrajectoryMessage> trajectory)
    {
        List<ChatMessage> messages = [new(ChatRole.User, query)];

        foreach (TrajectoryMessage turn in trajectory)
        {
            ChatRole role = new(turn.Role);

            if (role == ChatRole.User && messages.Count == 1)
            {
                // The captured trajectory repeats the opening question; do not duplicate it.
                continue;
            }

            List<AIContent> contents = [];

            if (!string.IsNullOrWhiteSpace(turn.Text))
            {
                contents.Add(new TextContent(turn.Text));
            }

            foreach (TrajectoryToolCall call in turn.ToolCalls)
            {
                contents.Add(new FunctionCallContent(call.CallId, call.Name, Normalise(call.Arguments)));
            }

            if (contents.Count > 0)
            {
                messages.Add(new ChatMessage(role, contents));
            }

            foreach (TrajectoryToolCall call in turn.ToolCalls.Where(c => c.Result is not null))
            {
                messages.Add(new ChatMessage(
                    ChatRole.Tool,
                    [new FunctionResultContent(call.CallId, call.Result)]));
            }
        }

        return messages;
    }

    /// <summary>Arguments survive a round trip as JsonElement; unwrap so the judge reads values.</summary>
    private static Dictionary<string, object?> Normalise(IReadOnlyDictionary<string, object?> arguments)
    {
        Dictionary<string, object?> normalised = new(StringComparer.Ordinal);

        foreach ((string key, object? value) in arguments)
        {
            normalised[key] = value is JsonElement element
                ? element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Number => element.TryGetInt64(out long l) ? l : element.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => element.ToString()
                }
                : value;
        }

        return normalised;
    }
}
