# Calibration labelling guide, v1

The criteria used to assign the human labels in `datasets/judge-calibration.jsonl`. Written down so
the labels can be reviewed, disputed, and reproduced by a second labeller.

This is **not** the judge's rubric. The quality evaluators ship their own prompts, which this
repository does not control. That is precisely why calibration exists: to measure how far the
judge's interpretation sits from ours, rather than to assume they agree.

## Retrieval, 1 to 5

How relevant are the retrieved chunks to the request, and how well ranked?

- **5** every chunk is on topic, best first
- **3** the right chunk is present but padded with noise, or ranked below a weaker one
- **1** nothing retrieved is relevant to the question asked

Precision counts, not just recall. Returning the correct chunk alongside two irrelevant ones is a 3,
because the noise displaces context the answer could have used.

## Groundedness, 1 to 5

Is every claim supported by the supplied context?

- **5** fully supported, nothing invented
- **3** mostly supported, with a claim that goes beyond the context without contradicting it
- **1** contradicts the context, or states specifics the context does not contain

Groundedness measures **support, not truth**. An answer that is correct in the real world but
unsupported by the provided context scores 1, because the model was working from memory rather than
evidence. A judge that rewards it is measuring its own knowledge.

Groundedness is independent of usefulness. A faithful restatement of the wrong document scores 5
here and 1 on relevance.

## Relevance, 1 to 5

Does the response address what the customer actually asked?

- **5** resolves the question and anticipates the obvious next step
- **3** addresses the topic but omits something the policy calls for
- **1** answers a different question, or resolves nothing

Padding costs at most one point. Formatting, tone, and required disclosures are checked
deterministically in Tier 1 and are not scored here.

## Known judgement call

An answer that makes no verifiable claim, such as "please contact us", is neither supported nor
contradicted. It is labelled **3** for groundedness rather than 5, because scoring evasion as
perfectly grounded would reward it. Reviewers who disagree should change `cal-10` and re-run
calibration.
