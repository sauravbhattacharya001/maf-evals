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
        RetrievalTrace? retrieval = recorder.LastRetrieval;
        int attempts = recorder.LastResponse?.Attempts ?? 1;
        return new RunTelemetry(retrieval, attempts, recorder.ToolCalls);
    }
}


