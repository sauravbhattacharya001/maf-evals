# Three-Tier Agent Evaluation

A reference implementation of a three-tier evaluation strategy for LLM agents, built on Microsoft
Agent Framework (.NET 8).

The tiers are not three sizes of the same test. They run in different places, at different times,
and answer different questions.

| | Tier 1 | Tier 2 | Tier 3 |
| --- | --- | --- | --- |
| Runs | inside the agent, every request | CI, every pull request | scheduled, or after an incident |
| Question | can this response go out? | may this change merge? | how reliable is it, and what went wrong? |
| Checks | tool arguments, then final response | rules, retrieval, RAG triad | repetition, intervals, incident replay |
| Model calls | none of its own | candidate + judge, cached | candidate + judge, many |
| On failure | retry, then per-rule severity | block the merge | report and explain |

## Core principle

Cheap deterministic checks run constantly. Expensive judge calls run rarely. Statistics run when
there is time to gather them. Each tier only does what the tier below it cannot.

- **Tier 1 is code, not a test.** It ships with the agent and fires on live traffic.
- **A rule beats a judge** whenever the property can be expressed as a rule. Judges are for
  relevance and groundedness, not for formatting or required disclosures.
- **Never pay twice for a response.** The triad scores responses that Tier 2 already generated.
- **One pass proves nothing**, which is why reliability claims live in Tier 3, not in the PR gate.

## Tier 1: hot-path guardrails

Two layers, both inside the ReAct loop.

**Layer A validates tool arguments before the tool runs.** On violation it does not call the tool;
it returns an explanation as the tool result, which the model reads on its next iteration and
corrects from. This costs nothing extra, because the loop was already going to iterate, and it
prevents side effects a later retry could not undo.

**Layer B validates the final response and retries with corrective feedback**, naming the rules that
failed.

Severity decides what happens when retries run out:

| Severity | Behaviour |
| --- | --- |
| `Warn` | record it, let the response through, do not spend a retry |
| `Retry` | retry, then degrade to a warning |
| `Block` | retry, then throw; the response must never reach a user |

```csharp
new AIAgentBuilder(inner)
    .Use(retrievalAugmenter)   // outermost: context fixed across retries
    .UseResponseGuard(guard)   // layer B
    .UseToolGuard(toolGuard)   // layer A, innermost
    .Build();
```

Retrieval sits outside the retry loop deliberately. If it ran inside, every retry would re-retrieve
and the captured trace would no longer describe the context that produced the final answer.

## Tier 2: the pull-request gate

One pass per case. Three kinds of check, in increasing cost:

1. **Rules** — the same engine Tier 1 uses, so a rule cannot drift between production and CI.
2. **Retrieval expectations** — did the expected corpus chunks come back? Deterministic, no judge.
3. **RAG triad** — retrieval, groundedness, and answer relevance, scored by a judge model.

Each triad metric separates a distinct failure: retrieval catches a bad knowledge base or query,
groundedness catches invention beyond the context, relevance catches well-grounded answers that
miss the question. A single quality score would blur all three.

### Threshold bands

A judge is stochastic, so a single cut-off makes a borderline score a coin flip that blocks a merge.
Each metric has two thresholds:

```json
{ "floor": 3.0, "target": 4.0 }
```

Below the floor blocks. Between floor and target warns. Deterministic checks have no band: they
always block, because they cannot flake.

## Tier 3: reliability and forensics

**Scheduled** (`tier3`): many repetitions, Wilson 95% intervals, per-case flakiness, and drift
against a baseline. With 5 passes out of 5 the naive interval reads 100% to 100%; Wilson reads
roughly 57% to 100%, which correctly says you have not yet earned a reliability claim.

**Incident** (`tier3 --incident`): replays a captured production trace against today's rules. Fully
offline. The outcomes are:

- Rules catch it: the guard now covers what production missed.
- Nothing catches it: add a golden case, or a recurrence will not be caught either.

## Quick start

```powershell
dotnet test                                     # 71 offline tests
dotnet run --project src/EvalRunner -- rules    # rule engine over frozen responses
dotnet run --project src/EvalRunner -- tier3 --incident incidents/sample-incident.json
```

All three run without credentials. Model-backed tiers need a key:

```powershell
$env:EVAL_API_KEY = "..."
$env:EVAL_MODEL   = "gpt-4o-mini"
$env:JUDGE_MODEL  = "gpt-4o"

dotnet run --project src/EvalRunner -- tier2
dotnet run --project src/EvalRunner -- tier3 --repetitions 5
```

## Commands

| Command | Purpose |
| --- | --- |
| `rules` | Rule engine over frozen responses, offline |
| `tier2 [--repetitions N] [--no-triad]` | PR gate; `--no-triad` skips judge calls |
| `tier3 [--repetitions N]` | Scheduled reliability run |
| `tier3 --incident PATH [--judge]` | Replay a production trace |
| `report [--run PATH]` | Print a saved run artifact |

Exit codes: `0` pass, `1` gate failure, `2` configuration error.

## Layout

```text
src/SupportAgent/Guardrails/     Tier 1: tool guard, response guard, severity policy
src/SupportAgent/Retrieval/      corpus loader, deterministic TF-IDF retriever
src/SupportAgent/SupportPolicy   rules declared inline with the agent
src/EvalFramework/Rules/         shared rule engine used by all three tiers
src/EvalFramework/RagTriad/      triad evaluators and threshold bands
src/EvalFramework/Statistics/    Wilson intervals, Tier 2 and Tier 3 gates
src/EvalFramework/Incident/      trace schema and replay
corpus/                          markdown knowledge base
datasets/                        golden set and frozen responses
incidents/                       captured production traces
config/eval-config.json          repetitions, thresholds, baseline
```

## Golden case schema

```json
{
  "id": "double-charge",
  "query": "I was charged twice for one order...",
  "critical": true,
  "expectedTerms": ["refund", "order number"],
  "forbiddenTerms": ["I can't help", "guaranteed refund"],
  "minLength": 60,
  "requireActionableFormat": true,
  "expectedChunkIds": ["refunds#1"],
  "severities": { "expected_terms": "Block" }
}
```

Rules live with the case, so extending coverage is a data change. `critical` cases face a stricter
Tier 3 gate. `severities` only affects Tier 1; Tier 2 gates on every rule regardless, because a
warn-level rule failing across the whole golden set is still a regression.

## Configuration

| Variable | Purpose |
| --- | --- |
| `EVAL_API_KEY` / `OPENAI_API_KEY` | Candidate agent credential |
| `EVAL_MODEL` | Candidate model, default `gpt-4o-mini` |
| `JUDGE_API_KEY`, `JUDGE_MODEL` | Judge, default `gpt-4o` |
| `EVAL_ENDPOINT`, `JUDGE_ENDPOINT` | Optional OpenAI-compatible base URLs |

The judge is configured separately on purpose. Grading a model with itself correlates exactly the
failure modes you most want to detect. Both clients are wrapped in a response cache, so re-running
an unchanged prompt is free.

## CI strategy

Offline tests, the rule check, and incident replay run on every pull request with no secrets.

Tier 2 needs credentials to generate responses at all, so **fork pull requests cannot run it**. They
receive an explicit skipped status rather than a misleading green, and a maintainer runs Tier 2 from
a branch in the repository before merging. Tier 3 runs on a schedule.

## Extending

**Add a case:** append a line to the JSONL file, add a frozen response, run `rules`. `GoldenSetTests`
fails if a case has no recorded response or if a frozen response breaks its own rules.

**Add a rule:** add it to `ResponseRules` or `ToolArgumentRules`, give it a default severity, and add
a test proving it fails when it should. Both tiers pick it up automatically.

**Add a tool guard:** declare a `ToolArgumentRule` in `SupportPolicy.ToolRules` next to the tool.

**After an incident:** capture a trace into `incidents/`, run replay, and add a golden case if the
report says the incident was unexplained by rules.

**Update the baseline:** only after an intentional, green Tier 3 run. Raising a baseline to silence a
failing gate defeats the purpose.

## Anti-patterns this design rejects

- Asserting exact model output in unit tests.
- Running a case once and calling the result a pass rate.
- Retrying until green and calling the flake fixed.
- Using a judge for anything a rule could check.
- Grading a model with itself.
- Comparing scores across different judge models or threshold settings.

## Limitations

- Tier 2 and scheduled Tier 3 need credentials and are not covered by the offline suite.
- Threshold bands are conventional starting values, not calibrated against human labels.
- The retriever is TF-IDF, chosen for reproducibility; a production system would swap in embeddings
  behind the same `IRetriever` interface.
- With a session, a Tier 1 retry sends only the correction, so the failed attempt stays in
  conversation history.

## References

- [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/)
- [.NET AI evaluation libraries](https://learn.microsoft.com/dotnet/ai/evaluation/libraries)
- [Wilson score interval](https://en.wikipedia.org/wiki/Binomial_proportion_confidence_interval)
