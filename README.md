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

Repeated runs bypass the response cache. Caching is what makes a judge affordable on every pull
request, but it is fatal here: identical prompts would return one stored answer, every repetition
would agree, and the interval would be computed over copies of a single observation. That does not
produce obviously broken output, it produces confident numbers that were never measured.

First live run, 5 cases at 5 repetitions: 100% pass rate, overall 95% CI 86.7% to 100%, no flaky
cases, and 5 of 5 textually distinct responses per case. The agent is nondeterministic in wording
and reliable in behaviour, which is exactly the distinction this tier exists to separate. Note the
per-case interval is 57% to 100% at n=5: five observations do not support a per-case reliability
claim, only the pooled figure does.

**Incident** (`tier3 --incident`): replays a captured production trace against today's rules. Fully
offline. The outcomes are:

- Rules catch it: the guard now covers what production missed.
- Nothing catches it: add a golden case, or a recurrence will not be caught either.

## Quick start

```powershell
dotnet test                                     # 194 offline tests
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
| `calibrate [--repeat N] [--case ID]` | Score the judge against human labels; `--repeat` measures self-consistency |

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
  "expectedAnyTerms": [["professional", "pharmacist", "doctor"]],
  "forbiddenTerms": ["I can't help", "guaranteed refund"],
  "minLength": 60,
  "requireActionableFormat": true,
  "expectedChunkIds": ["refunds#1"],
  "severities": { "expected_terms": "Block" }
}
```

Rules live with the case, so extending coverage is a data change. `critical` cases face a stricter
Tier 3 gate. `expectedTerms` requires every term; `expectedAnyTerms` requires one term from each
group, which is what you need when several words satisfy the same policy ("professional" or
"pharmacist"). `severities` only affects Tier 1; Tier 2 gates on every rule regardless, because a
warn-level rule failing across the whole golden set is still a regression.

## What the first live run caught

The gate failed on its first real run, which is the outcome it exists for. All three findings were
defects in this repository, not in the agent:

| Finding | Evidence | Fix |
| --- | --- | --- |
| Retrieval missed the shipping policy for "has not arrived" | deterministic chunk check **and** judge Retrieval 1.0 | query expansion, since customers and policies use different words |
| A rule rejected correct behaviour: the agent said "consult your pharmacist" but the rule demanded "professional" | rule failure on a good response | added `expectedAnyTerms` for alternatives |
| The response cache never hit in CI | `MemoryDistributedCache` is per-process | file-backed cache under `artifacts/cache` |

After the fixes the gate passed, retrieval for that case went from 1.0 to 4.0, and a re-run served
entirely from cache: 145 ms mean latency instead of 1883 ms, at no API cost. Each finding is now
pinned by a regression test in `LiveRunRegressionTests`.

Note the third finding was a documentation lie, not just a bug: the README claimed caching made
re-runs free while the implementation could not deliver it. The code was fixed rather than the claim
softened.

## Configuration

| Variable | Purpose |
| --- | --- |
| `EVAL_API_KEY` / `OPENAI_API_KEY` | Candidate agent credential |
| `EVAL_MODEL` | Candidate model, default `gpt-4o-mini` |
| `JUDGE_API_KEY`, `JUDGE_MODEL` | Judge, default `gpt-4o` |
| `EVAL_ENDPOINT`, `JUDGE_ENDPOINT` | Optional OpenAI-compatible base URLs |

The judge is configured separately on purpose. Grading a model with itself correlates exactly the
failure modes you most want to detect. Both clients are wrapped in a file-backed response cache
under `artifacts/cache`, so re-running an unchanged prompt costs nothing even in a fresh CI process.
Measured: a full Tier 2 run costs under `artifacts/cache`, so re-running an unchanged prompt costs nothing even in a fresh CI process..0996 cold and under `artifacts/cache`, so re-running an unchanged prompt costs nothing even in a fresh CI process..0000 warm.

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

## Judge calibration

Thresholds are only meaningful if the judge's 3 means what a reviewer's 3 means. `datasets/judge-calibration.jsonl`
holds 12 hand-labelled cases, deliberately built so the three metrics diverge, and
`dotnet run --project src/EvalRunner -- calibrate` scores the judge against them.

Two questions get asked, in order. Does the judge agree with a human, and does it agree with itself?
The second must be answered first, because a metric that contradicts itself cannot agree with
anything.

### Self-consistency, 12 cases judged 3 times each

| Metric | mean SD | worst range | verdict flip rate |
| --- | --- | --- | --- |
| Retrieval | 0.20 | 3.0 | **17%** |
| Groundedness | 0.00 | 0.0 | 0% |
| Relevance | 0.00 | 0.0 | 0% |

Retrieval scored one identical input `5, 2, 4, 5, 2` across five runs. Mean standard deviation of
0.20 looks reassuring, and is misleading: most cases are stable while two swing by three points, so
17% of cases would flip a merge decision at random. The flip rate is the number that matters, not
the average.

**Retrieval is therefore advisory, not blocking.** Retrieval quality is gated by `expectedChunkIds`
instead, which is exact, stable, and free. Groundedness and relevance were bit-for-bit reproducible,
so thresholds on them are meaningful.

### Agreement with human labels

| Metric | exact | within 1 | MAE | bias | corr | band |
| --- | --- | --- | --- | --- | --- | --- |
| Retrieval | 75% | 92% | 0.42 | -0.42 | 0.88 | 83% |
| Groundedness | 42% | 67% | 1.17 | -0.17 | 0.44 | 75% |
| Relevance | 25% | 83% | 0.92 | -0.42 | 0.74 | 83% |

Groundedness fails in two opposite directions at once:

| Failure | Cases | Human | Judge |
| --- | --- | --- | --- |
| Fabrication or contradiction | cal-02, cal-06, cal-11 | 1, 1, 2 | **3.0 every time** |
| Faithful but off-topic | cal-03, cal-12 | 5, 4 | **1, 2** |

The judge never uses 1 or 2 for fabrication, and it penalises groundedness for irrelevance, blending
the two metrics it exists to separate. Because the errors cancel, **bias reads a healthy −0.17 while
the metric is unreliable**; mean absolute error and band agreement tell the truth. Correlation and
bias alone are not sufficient evidence for a threshold.

The floor was raised from 3.0 to 3.5 as a direct consequence: at 3.0 every hallucination case scored
exactly 3.0 and passed as a warning, so the metric designed to catch invention never fired on it.
That single change raised band agreement from 50% to 75%.

Re-run calibration after changing the judge model, the rubric, or any threshold. Numbers from
different judges are not comparable.

## How the framework checks itself

An eval framework that cannot fail is indistinguishable from one that always returns "passed". These
guards exist to keep that honest:

| Guard | What it prevents |
| --- | --- |
| Negative fixtures | Rules that never fire. Each known-bad response must fail *via a named rule* |
| Seeded defects | An unwired pipeline. Real agent, guardrails, retriever, runner, then assert the gate reacts |
| Retrieval regressions | Paying a judge to discover a retrieval break the free suite could catch |
| Wilson coverage simulation | Trusting a transcribed formula instead of the property it claims |
| Golden-set health | Silent dataset decay: duplicates, unexercised corpus, cases with no content rule |
| Schema fixtures | Artifacts becoming unreadable while still claiming a version |
| Judge calibration | Thresholds set by taste rather than measurement |

Mutation check: forcing every rule to pass breaks 31 of the tests. Line coverage is 86.6%, branch
74.3%, collected on every pull request.

### Concurrency boundary

Judging is parallel with a bounded budget; running the agent is deliberately sequential. Telemetry
is captured through a per-agent recorder reset before each invocation, so concurrent runs would
interleave one another's retrieval traces and retry counts. That would corrupt the evidence to save
wall-clock time. Judging reads a recorded response and writes nothing shared, so it parallelises
safely and returns results in input order for stable artifacts.

Agent and judge calls both have a timeout (`callTimeoutSeconds`, default 120). A timed-out call is
recorded as `Errored`, never as an agent failure.

## Cost

Every run records billed calls, tokens, and estimated cost for the candidate and the judge
separately. Usage tracking sits **below** the response cache, so a cache hit costs nothing and is
never reported as spend; without that ordering the saving caching provides could not be verified.

Measured on the 5-case golden set:

| | Cold cache | Warm cache |
| --- | --- | --- |
| Candidate (`gpt-4o-mini`) | 5 calls, 1,797 tokens, $0.0004 | 0 calls, $0.0000 |
| Judge (`gpt-4o`) | 15 calls, 32,087 tokens, $0.0992 | 0 calls, $0.0000 |
| **Total** | **$0.0996** | **$0.0000** |

The judge costs roughly **250 times** the agent: 18 times the tokens on a model priced an order of
magnitude higher. The system under test is nearly free, and measuring it is the entire bill. Three
consequences follow.

- Caching is not a convenience, it is what makes a per-pull-request judge viable.
- Judge sampling matters far more than candidate sampling. Tier 3 repeats the *agent* cheaply; it
  does not repeat the judge.
- Roughly a third of judge spend goes on retrieval scoring, which is advisory and flips 17% of
  verdicts. Dropping it is a defensible saving, kept for now because it is still informative when
  read as a trend rather than a gate.

`maxRunCostUsd` gates the total. An unpriced model yields no cost rather than zero, and the budget
gate reports that it could not be enforced instead of silently passing.

## Limitations

- Tier 2 and scheduled Tier 3 need credentials and are not covered by the offline suite.
- **Wilson coverage dips near the boundary.** Simulation over 2000 samples measured 91.4% coverage
  at a true rate of 0.98 with n=25, against a nominal 95%. Coverage oscillates with the discreteness
  of the binomial and is weakest as the rate approaches 1, which is exactly where a healthy agent
  sits. The Tier 3 lower-bound gate is therefore slightly anti-conservative in its most common
  operating region. Raising repetitions is the mitigation; the effect is pinned by
  `WilsonCoverageTests`.
- Groundedness agrees with human labels only 75% of the time at band level, and relevance 83%. Both
  are gates today; treat their scores as coarse signals rather than measurements.
- The calibration set is 12 cases labelled by one person. It is enough to expose systematic judge
  behaviour, not enough to tune thresholds finely.
- The retriever is TF-IDF, chosen for reproducibility; a production system would swap in embeddings
  behind the same `IRetriever` interface.
- With a session, a Tier 1 retry sends only the correction, so the failed attempt stays in
  conversation history.

## References

- [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/)
- [.NET AI evaluation libraries](https://learn.microsoft.com/dotnet/ai/evaluation/libraries)
- [Wilson score interval](https://en.wikipedia.org/wiki/Binomial_proportion_confidence_interval)





