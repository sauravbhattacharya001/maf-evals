# Three-Tier Agent Evaluation

A working reference implementation of a three-tier evaluation strategy for LLM agents, built on
Microsoft Agent Framework (.NET 8).

The tiers are separated because they answer different questions:

| Tier | Question | Model calls | Deterministic | CI |
| --- | --- | --- | --- | --- |
| 1. Deterministic | Do known-good responses still satisfy our rules? | none | yes | every PR |
| 2. Statistical golden set | How reliably does the real agent satisfy those rules? | candidate only | scoring is | scheduled / manual |
| 3. Model as judge | Is the output actually any good? | judge only | no | scheduled / manual |

## Core principle

Most agent evaluation fails in one of two ways: asserting exact strings against a stochastic system,
or asking a model to grade everything. This framework avoids both.

- **If a rule can be written as code, it belongs in Tier 1.** Rules are cheap, instant, and
  never disagree with themselves.
- **A single run proves nothing.** Tier 2 repeats every case and reports a confidence interval,
  because a 5-for-5 pass is not evidence of a 100% pass rate.
- **A judge should only score what a rule cannot.** Tier 3 covers relevance and coherence, not
  formatting or required disclosures.
- **Never pay twice for the same response.** Tier 3 reads the responses Tier 2 already recorded.

## Data flow

```text
datasets/support-golden-set.jsonl
        |
        |  frozen responses                 live agent, N repetitions
        v                                            v
  Tier 1  ──────── same scorer ────────────────►  Tier 2
  pass/fail                                    pass rate + Wilson CI + gates
                                                     |
                                                     v
                                          artifacts/runs/tier2-<id>.json
                                                     |
                                                     v
                                                  Tier 3
                                        judge scores over saved responses
                                                     |
                                                     v
                                          artifacts/runs/tier3-<id>.json
```

Tier 1 and Tier 2 call the exact same scorer (`DeterministicEvaluator`). Tier 1 proves the rules
behave correctly; Tier 2 measures how often a real agent satisfies them.

## Quick start

```powershell
dotnet test                                    # framework unit tests, offline
dotnet run --project src/EvalRunner -- tier1   # deterministic checks, offline
```

Model-backed tiers need credentials:

```powershell
$env:EVAL_API_KEY = "..."
$env:EVAL_MODEL   = "gpt-4o-mini"

dotnet run --project src/EvalRunner -- tier2
dotnet run --project src/EvalRunner -- tier3
```

## Commands

| Command | Purpose |
| --- | --- |
| `tier1` | Deterministic rules over frozen responses |
| `tier2 [--repetitions N]` | Run the agent over the golden set, apply statistical gates |
| `tier3 [--run PATH] [--sample-per-case N] [--min-mean X] [--min-score X]` | Judge a Tier 2 artifact |
| `report [--run PATH]` | Print the Markdown report for a Tier 2 artifact |

Exit codes: `0` pass, `1` gate failure, `2` configuration error. Tier 3 defaults to the most recent
Tier 2 artifact when `--run` is omitted.

## Layout

```text
datasets/support-golden-set.jsonl        golden cases, one JSON object per line
datasets/tier1-recorded-responses.json   frozen responses for Tier 1
config/eval-config.json                  repetitions, thresholds, baseline
rubrics/support-quality-v1.md            versioned judge rubric
src/EvalFramework/Deterministic/         Tier 1 rules
src/EvalFramework/Execution/             Tier 2 runner and run artifact schema
src/EvalFramework/Statistics/            Wilson intervals, aggregation, gates
src/EvalFramework/Judging/               Tier 3 judging and score aggregation
src/EvalRunner/                          CLI
artifacts/runs/                          versioned run artifacts
```

## Golden case schema

```json
{
  "id": "double-charge",
  "query": "I was charged twice for one order. How do I get the extra charge refunded?",
  "critical": true,
  "expectedTerms": ["refund", "order number"],
  "forbiddenTerms": ["I can't help", "guaranteed"],
  "minLength": 60,
  "requireActionableFormat": true
}
```

Rules live with the case, so extending coverage is usually a data change rather than a code change.
`critical` cases are held to a stricter Tier 2 gate: they are the behaviours you are unwilling to
regress, such as refusing to give medical advice.

## Why Wilson intervals

With 5 repetitions and 5 passes, the naive interval is 100% to 100%. That is the exact false
confidence Tier 2 exists to prevent. The Wilson score interval gives roughly 57% to 100% for that
sample, correctly saying you have not yet earned a strong reliability claim.

Consequences for gate design:

- The overall gate compares the **lower bound** against `minOverallPassRate`, so passing requires
  either a high pass rate or enough repetitions.
- Per-case gates use the observed rate, since critical cases are typically all-or-nothing.
- A case that both passes and fails across repetitions is reported as `flaky`. Flakiness is a
  finding, not noise to be retried away.

## Configuration

`config/eval-config.json`:

| Field | Meaning |
| --- | --- |
| `repetitions` | Runs per case, default 5 |
| `minOverallPassRate` | Required 95% lower bound overall |
| `minCriticalCasePassRate` | Required rate for critical cases |
| `minStandardCasePassRate` | Required rate for other cases |
| `maxRegression` | Allowed drop from `baselineOverallPassRate` |
| `baselineOverallPassRate` | Last approved pass rate, `null` until you set one |
| `maxMeanLatencyMs` | Optional latency budget |

Environment variables:

| Variable | Purpose |
| --- | --- |
| `EVAL_API_KEY` / `OPENAI_API_KEY` | Candidate agent credential |
| `EVAL_MODEL` | Candidate model, default `gpt-4o-mini` |
| `EVAL_ENDPOINT` | Optional OpenAI-compatible base URL |
| `JUDGE_API_KEY`, `JUDGE_MODEL`, `JUDGE_ENDPOINT` | Judge configuration, defaults to `gpt-4o` |

The judge is configured separately on purpose. Using one model to grade itself correlates the
failure modes you most want to detect.

## CI strategy

`.github/workflows/evals.yml` runs Tier 1 and the unit tests on every pull request, with no secrets.
Tiers 2 and 3 run on a schedule or on demand, and upload their artifacts. This keeps PR feedback
fast and deterministic while reliability and quality are tracked over time rather than per commit.

## Extending

**Add a case:** append a line to the JSONL file, add a frozen response for Tier 1, run `tier1`.
`GoldenSetTests` fails if a case has no recorded response or if a frozen response violates its own
rules.

**Add a rule:** add a check to `DeterministicEvaluator` and a test showing it fails when it should.
Prefer data-driven rules on `GoldenCase` over hard-coded logic.

**Change the rubric:** copy `rubrics/support-quality-v1.md` to a new version and update the version
string. Never edit a rubric in place, because scores from different rubrics are not comparable.

**Update the baseline:** only after a Tier 2 run is green and the change is intentional. Record the
new `baselineOverallPassRate`. Raising a baseline to make a failing gate pass defeats the purpose.

## Anti-patterns this design rejects

- Asserting exact model output in unit tests.
- Running each case once and treating the result as a pass rate.
- Retrying until green and calling the flake resolved.
- Using a judge for anything a rule could check.
- Grading a model with itself.
- Comparing scores across different judge models or rubric versions.

## Cost and reproducibility

Tier 1 is free and fully reproducible. Tier 2 cost scales with cases times repetitions. Tier 3
defaults to `--sample-per-case 1`, since judging every repetition is usually wasteful when Tier 2
already measured consistency.

Every run artifact records the model, dataset path, repetition count, timestamp, and full responses,
so any reported number can be traced back to the exact outputs that produced it.

## Limitations

- Tier 2 and Tier 3 require credentials and are not exercised by the offline test suite.
- Judge scores are calibrated for stronger judge models; small local models degrade rubric adherence.
- Groundedness and tool-call accuracy are not yet wired in; they need per-case context and tool
  definitions on the golden case schema.
- There is no human-label calibration set yet, so judge thresholds are conventional rather than
  empirically tuned.

## References

- [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/)
- [.NET AI evaluation libraries](https://learn.microsoft.com/dotnet/ai/evaluation/libraries)
- [Wilson score interval](https://en.wikipedia.org/wiki/Binomial_proportion_confidence_interval)
