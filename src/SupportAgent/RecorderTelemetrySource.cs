using EvalFramework.Execution;
using EvalFramework.Retrieval;

namespace SupportAgent;

/// <summary>
/// Bridges the agent's guardrail telemetry to the eval runner without the framework taking a
/// dependency on the agent project.
/// </summary>
public sealed class RecorderTelemetrySource(GuardrailRecorder recorder) : IRunTelemetrySource
{
    public void Reset() => recorder.Reset();

    public RunTelemetry Capture()
    {
        // A conversation searches once per turn; the expectation applies to the whole conversation.
        RetrievalTrace? retrieval = recorder.Retrievals.Count == 0
            ? null
            : RetrievalTrace.Merge(recorder.Retrievals);
        int attempts = recorder.LastResponse?.Attempts ?? 1;
        return new RunTelemetry(retrieval, attempts, recorder.ToolCalls);
    }
}



