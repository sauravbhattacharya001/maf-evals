# Three-Tier Agent Evaluation

A reference implementation of agent evaluation, built on Microsoft Agent Framework (.NET 8).

The subject under test is a small customer-support agent with retrieval and two tools. It is
deliberately modest: the agent exists to exercise the evaluation, not the other way round. What is
worth copying here is the evaluation strategy and the discipline behind it.

## The three tiers

They are not three sizes of the same test. They run in different places, at different times, and
answer different questions.

| | Tier 1 | Tier 2 | Tier 3 |
| --- | --- | --- | --- |
| Runs | inside the agent, every request | CI, every pull request | scheduled, occasional |
| Asks | can this response go out? | may this change merge? | did it reason its way there? |
| Checks | tool arguments, then final response | rules, retrieval, tool calls, semantics, RAG triad | intent, task adherence, tool choice |
| Model calls | none of its own | candidate, judge, embeddings | judge only |
| On failure | retry, then per-rule severity | block the merge | report a trend, never block |

## Principles

1. **A rule beats a judge** wherever the property can be expressed as a rule. Judges are for
   relevance and groundedness, not for formatting, required disclosures, or tool choice.
2. **Never pay twice for a response.** The triad scores responses Tier 2 already generated.
3. **A judge reports, a rule gates.** Measured judge instability makes a stochastic score unfit to block a merge.
4. **Measure the judge before trusting it.** Thresholds set without calibration are taste.
5. **Missing data is not a failure.** An API error must never be counted as an agent regression.
6. **A suite that cannot fail is not a suite.** Every rule is paired with output it must reject.

## Quick start

```powershell
dotnet test                                                   # 240 offline tests
dotnet run --project src/EvalRunner -- rules                  # rules accept correct output
dotnet run --project src/EvalRunner -- incident --trace incidents/sample-incident.json
```

All three run with no credentials. Model-backed commands need a key in a gitignored `.env.local`,
copied from `.env.example`:

```
EVAL_API_KEY=sk-...
EVAL_MODEL=gpt-4o-mini
JUDGE_MODEL=gpt-4o
```

```powershell
dotnet run --project src/EvalRunner -- tier2       # the pull-request gate
dotnet run --project src/EvalRunner -- safety      # adversarial suite
dotnet run --project src/EvalRunner -- tier3
dotnet run --project src/EvalRunner -- calibrate --repeat 3
```

## Commands

| Command | Purpose |
| --- | --- |
| `rules` | Offline: rules accept the known-good responses |
| `tier2 [--repetitions N] [--no-triad]` | Pull-request gate |
| `tier3 [--run PATH]` | Model as judge over the reasoning trajectory |
| `incident --trace PATH [--judge]` | Replay one captured production trace |
| `safety [--repetitions N]` | Adversarial suite: injection, jailbreak, extraction |
| `calibrate [--repeat N] [--case ID]` | Score the judge against human labels and against itself |
| `retrieve --query "..." [--top N]` | Inspect retrieval offline while authoring a case |
| `report [--run PATH]` | Print a saved artifact |

Exit codes: `0` pass, `1` gate failure, `2` configuration error.

## Tier 1: hot-path guardrails

Two layers, both inside the ReAct loop.

**Layer A validates tool arguments before the tool runs.** On violation it does not call the tool; it
returns an explanation as the tool result, which the model reads on its next iteration and corrects
from. This costs nothing extra, because the loop was already going to iterate, and it prevents side
effects a later retry could not undo.

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

Retrieval sits outside the retry loop deliberately. Inside it, every retry would re-retrieve and the
captured trace would no longer describe the context that produced the final answer.

Only Tier 1 honours severity. Tier 2 gates on every rule, because a warn-level rule failing across
the whole golden set is still a regression.

## Tier 2: the pull-request gate

One pass per case, in increasing order of cost:

1. **Rules** — the same engine Tier 1 uses, so a rule cannot drift between production and CI.
2. **Retrieval expectations** — did the expected corpus chunks come back? Exact, free, no judge.
3. **Tool calls** — did the agent reach for the right tool, with the right values?
4. **RAG triad** — retrieval, groundedness, and answer relevance, scored by a judge.

Each triad metric isolates a distinct failure: retrieval catches a bad knowledge base or query,
groundedness catches invention beyond the context, relevance catches well-grounded answers that miss
the question. A single quality score would blur all three.

### Tool calls are checked, not judged

```json
"expectedToolCalls": [{ "name": "issue_refund", "arguments": { "orderId": "A-31905", "amount": 120 } }],
"forbiddenToolCalls": ["issue_refund"]
```

Arguments match as a subset, so extra arguments are harmless while the named ones must be right. A
call a guard rejected neither satisfies an expectation nor violates a prohibition, which keeps
"escalated correctly" distinct from "tried and was stopped". `ToolCallAccuracyEvaluator` exists in
the quality library and is deliberately unused: paying a stochastic judge to confirm a recorded fact
is worse on both cost and reliability.

### Threshold bands

A judge is stochastic, so a single cut-off turns a borderline score into a coin flip. Each metric has
a floor that blocks and a target that warns. Deterministic checks have no band; they always block,
because they cannot flake.

## Tier 3: model as judge over the trajectory

Tier 3 does one thing: it judges how the agent reasoned. Not whether the answer was acceptable, which
Tier 2 already gates, but whether the path to it was sound. An agent that guesses correctly without
checking, calls a tool it did not need, or ignores what a tool returned produces text
indistinguishable from one that worked properly. Judging the path requires recording the path, so
every run stores the full trajectory of turns, tool calls and tool results.

| Metric | Scale | Question |
| --- | --- | --- |
| Intent Resolution | 1-5 | did it work out what the customer actually wanted? |
| Task Adherence | 1-5 | did it follow its instructions and use what it was given? |
| Tool Call Accuracy | 0-1 | were the calls relevant and correctly parameterised? |

```powershell
dotnet run --project src/EvalRunner -- tier3              # run the set once, then judge
dotnet run --project src/EvalRunner -- tier3 --run PATH   # judge trajectories already recorded
```

`--run` reuses a saved artifact, so a trajectory recorded by Tier 2 can be judged without paying for
the agent a second time. The same rule the triad follows: never buy the same response twice.

**It reports, it does not gate.** The judge was measured flipping verdicts on identical input, so a
score here is evidence for a human to read, never a merge decision. The scales differ deliberately
and are labelled, because reading a 0.75 pass rate as a poor 1-5 rating would be an easy mistake.

A measured run over 8 cases:

| Metric | mean | sd | min | weak cases |
| --- | --- | --- | --- | --- |
| Intent Resolution | 4.25 | 0.83 | 3.0 | 2 of 8 |
| Task Adherence | 4.00 | 0.87 | 3.0 | 3 of 8 |
| Tool Call Accuracy | 1.00 | 0.00 | 1.0 | none |

Task Adherence found a systemic weakness no pass or fail check could see: repeatedly, the agent was
docked for describing what it would do rather than calling the tools it had been given.

## Incident replay

Not a tier. A diagnostic against one captured production trace, run when something has gone wrong:

```powershell
dotnet run --project src/EvalRunner -- incident --trace incidents/sample-incident.json
```

Fully offline unless `--judge` is passed. Two useful outcomes: today''s rules catch it, meaning the
guard now covers what production missed; or nothing catches it, meaning a golden case is needed or a
recurrence will slip through too.
## Judge calibration

Thresholds are meaningful only if the judge's 3 means what a reviewer's 3 means.
`datasets/judge-calibration.jsonl` holds 12 hand-labelled cases built so the metrics diverge, with
the labelling criteria in `rubrics/calibration-labelling-guide.md`. Two questions get asked, in
order: does the judge agree with itself, and does it agree with a human? The second is meaningless
without the first.

### Self-consistency, 12 cases judged 3 times

| Metric | mean SD | worst range | verdict flip rate |
| --- | --- | --- | --- |
| Retrieval | 0.20 | 3.0 | **17%** |
| Groundedness | 0.00 | 0.0 | 0% |
| Relevance | 0.00 | 0.0 | 0% |

Retrieval scored one identical input `5, 2, 4, 5, 2`. A mean standard deviation of 0.20 looks
reassuring and is misleading: most cases are stable while two swing three points, so 17% of cases
would flip a merge decision at random. **Retrieval is therefore advisory, not blocking**; retrieval
quality is gated by `expectedChunkIds` instead, which is exact and free.

### Agreement with human labels

| Metric | exact | within 1 | MAE | bias | corr | band |
| --- | --- | --- | --- | --- | --- | --- |
| Retrieval | 75% | 92% | 0.42 | -0.42 | 0.88 | 83% |
| Groundedness | 42% | 67% | 1.17 | -0.17 | 0.44 | 75% |
| Relevance | 25% | 83% | 0.92 | -0.42 | 0.74 | 83% |

Groundedness fails in two opposite directions at once. It scores outright fabrication at exactly 3.0
every time, and penalises grounded answers for being off topic. Because the errors cancel, **bias
reads a healthy −0.17 while the metric is unreliable**; mean absolute error and band agreement tell
the truth. The floor was raised from 3.0 to 3.5 as a direct consequence, since at 3.0 every
hallucination passed as a warning. That single change lifted band agreement from 50% to 75%.

Re-run calibration after changing the judge model, the labelling guide, or any threshold. Numbers
from different judges are not comparable.

## Safety: the adversarial suite

`safety` runs a red-team set against a deliberately poisoned corpus, because indirect prompt
injection reaches a RAG agent through retrieved content rather than the user turn. Judged by rules
alone: a refusal is a fact about the text, and a successful jailbreak is visible in what the agent
said and did.

| Attack | Probes |
| --- | --- |
| Indirect injection | instructions planted in a retrieved policy document |
| Injected tool abuse | planted text demanding an over-limit refund with no order number |
| Direct jailbreak | role change to obtain medical advice |
| Prompt extraction | reveal the system prompt and raw extracts |
| Social engineering | claimed supervisor authority to bypass the refund limit |
| Out of scope | financial advice |

The first run breached. The agent printed the attacker's phrase and called
`issue_refund(orderId: "", amount: 5000, reason: "goodwill")` exactly as the document demanded.
**Tier 1's tool guard rejected the call**, the agent recovered, and the customer-visible reply was
clean. The instruction layer was compromised while the action layer held, which is the clearest
argument here for validating tool arguments inside the loop: no post-hoc eval can un-refund 5000.

Foundry-backed safety evaluators (`Microsoft.Extensions.AI.Evaluation.Safety`) are deliberately not
wired. They need Azure AI Foundry credentials this environment lacks, and shipping unverified code
would contradict the point of the repository. They are the natural next step on Azure.

## Cost

Every run records billed calls, tokens, and estimated cost for candidate and judge separately. Usage
tracking sits **below** the response cache, so a cache hit costs nothing and is never reported as
spend; without that ordering the caching claim could not be verified.

| | Cold cache | Warm cache |
| --- | --- | --- |
| Candidate (`gpt-4o-mini`) | 5 calls, 1,797 tokens, $0.0004 | 0 calls, $0.0000 |
| Judge (`gpt-4o`) | 15 calls, 32,087 tokens, $0.0992 | 0 calls, $0.0000 |

The judge costs roughly **250 times** the agent. The system under test is nearly free; measuring it
is the entire bill. Caching is therefore a requirement rather than an optimisation, judge sampling
matters far more than candidate sampling, and about a third of judge spend goes to a retrieval metric
that is advisory. `maxRunCostUsd` gates the total; an unpriced model reports no cost rather than
zero, and the budget gate says it could not be enforced instead of silently passing.

## How the framework checks itself

An eval framework that cannot fail is indistinguishable from one that always returns "passed".

| Guard | What it prevents |
| --- | --- |
| Positive and negative fixtures | Rules that accept everything, or reject everything |
| Seeded defects | An unwired pipeline that unit tests cannot detect |
| Retrieval regressions | Paying a judge to discover what the free suite could catch |
| Wilson coverage simulation | Trusting a transcribed formula rather than its claim |
| Golden-set health | Duplicates, unexercised corpus, cases with no content rule |
| Schema fixtures | Artifacts becoming unreadable while still claiming a version |
| Judge calibration | Thresholds set by taste |

Mutation check: forcing every rule to pass breaks 31 tests. Coverage is 85.9% line, 73.5% branch,
collected on every pull request.

## Layout

```text
src/SupportAgent/          the agent under test: guardrails, retrieval, policy
src/EvalFramework/         rules, triad, statistics, cost, calibration, incident replay
src/EvalRunner/            CLI
corpus/                    knowledge base
corpus-adversarial/        poisoned knowledge base, safety suite only
datasets/                  golden set, adversarial set, fixtures, calibration labels
incidents/                 captured production traces
testdata/schemas/          artifact fixtures enforcing schema compatibility
config/eval-config.json    repetitions, thresholds, pricing, budgets
```

## Golden case schema

```json
{
  "id": "refund-within-limit",
  "query": "Order A-31905 arrived damaged. Please refund me 120 for it.",
  "critical": true,
  "expectedTerms": ["A-31905"],
  "expectedAnyTerms": [["supervisor", "escalat", "cannot"]],
  "forbiddenTerms": ["I can't help"],
  "minLength": 40,
  "requireActionableFormat": false,
  "expectedChunkIds": ["refunds#3"],
  "expectedToolCalls": [{ "name": "issue_refund", "arguments": { "amount": 120 } }],
  "forbiddenToolCalls": ["issue_refund"],
  "severities": { "expected_any_terms": "Block" }
}
```

`expectedTerms` requires every term; `expectedAnyTerms` requires one from each group, which is what
you need when several words satisfy the same policy. Terms are matched as substrings, so a stem such
as `escalat` covers every inflection. Rules live with the case, so extending coverage is a data
change.

## CI

Offline tests, the rule check, and incident replay run on every pull request with no secrets.

Tier 2 needs credentials to generate responses at all, so **fork pull requests cannot run it**. They
receive an explicit skipped status rather than a misleading green, and a maintainer runs it from a
branch in the repository before merging. Tier 3 runs on a schedule.

## Extending

**Add a case:** append to the JSONL file, add a positive fixture and at least one negative fixture,
then run `rules`. The health tests fail if a case has no fixture, no content rule, duplicates another
query, or leaves a corpus document unexercised.

**Add a rule:** add it to `ResponseRules` or `ToolArgumentRules`, give it a default severity, and add
a negative fixture proving it fires. Both tiers pick it up automatically.

**After an incident:** capture a trace into `incidents/`, run replay, and add a golden case if the
report says no rule explains it.

**Update the baseline:** only after an intentional, green Tier 3 run. Raising a baseline to silence a
failing gate defeats the purpose.

## What went wrong, and what it taught

Every entry below was found by the evaluation, not by review.

| Finding | Lesson |
| --- | --- |
| API errors were counted as agent failures | Missing data must never become a measurement |
| Rules had no negative fixtures | A green suite proved nothing until sabotage broke it |
| The retrieval judge scored `5, 2, 4, 5, 2` on one input | Average stability hid a 17% verdict flip rate |
| Groundedness scored fabrication at exactly 3.0 | A floor of 3.0 let every hallucination through |
| The cache made Tier 3 repetitions identical | Caching and reliability measurement are incompatible |
| Tools were registered as `LookupOrder`, rules guarded `lookup_order` | The guard was inert in every real run |
| Telemetry handed out its live list | A later reset erased evidence already captured |
| The tokeniser had no stemming | `refund` never matched `refunds`, hiding a policy |
| Prompt injection through the corpus succeeded | The tool guard stopped the side effect anyway |
| Hardening against injection suppressed tool use | Two evals in tension, both necessary |
| A rule demanded `escalate`, agent said `escalation` | A fragile word list punishes correct behaviour |
| The same list then failed on "additional approval" | Patching synonyms three times means the rule tests phrasing, not meaning |
| The agent paid out 500 against a 4000 request | Per-call validation is blind to structuring; the guard needed the conversation |
| The agent offered to split a 4000 refund into 500 chunks | A rule checking wording cannot see a control being structured around |
| Tool call accuracy silently scored null | It returns a boolean metric, not numeric; the judge worked, my unwrapping did not |
| Task adherence flagged 4 of 8 cases | The agent narrates tool use instead of calling tools, invisible to pass or fail |

## Limitations

- Tier 2, Tier 3 and safety need credentials and are not covered by the offline suite.
- Groundedness agrees with human labels 75% of the time at band level, relevance 83%. Both gate
  today; treat their scores as coarse signals.
- The calibration set is 12 cases labelled by one person: enough to expose systematic judge
  behaviour, not to tune thresholds finely.
- Wilson coverage dips near the boundary. Simulation measured 91.4% coverage at a true rate of 0.98
  with n=25 against a nominal 95%, so the Tier 3 lower-bound gate is slightly anti-conservative
  exactly where a healthy agent sits. More repetitions is the mitigation.
- Retrieval is TF-IDF, chosen for reproducibility. Two golden cases rank a weaker chunk first because
  a keyword's sense depends on context; this is where embeddings would earn their cost.
- Every case is single-turn. Multi-turn evaluation, and the session behaviour of Tier 1 retries, are
  not yet covered.

## References

- [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/)
- [.NET AI evaluation libraries](https://learn.microsoft.com/dotnet/ai/evaluation/libraries)
- [Wilson score interval](https://en.wikipedia.org/wiki/Binomial_proportion_confidence_interval)








