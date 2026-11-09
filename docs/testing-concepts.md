# Diffra in testing terms

Diffra is a form of **differential testing**: it judges a change by comparing what the software
produces now with what it produced at a trusted earlier point. This page places Diffra among the
testing techniques it borrows from, and says what it adds to them.

## The oracle problem

Every test needs an oracle: something that says whether an output is right. For many outputs worth
checking there is no practical oracle. Nobody can state in advance the correct generated source for
a schema, the right public API surface of a library, or the acceptable cyclomatic complexity of a
module. What a team *can* say is whether a change to one of these was intended.

Differential testing answers that by changing the question. Instead of "is this output correct?",
it asks "how does this output differ from a reference I already trust?" The reference can be
another implementation of the same specification (McKeeman's original use, for C compilers), the
same program on different inputs that should agree, or the same program at an earlier commit.
Diffra uses the last: the reference is the evidence produced at an exact, trusted baseline commit.

## Techniques Diffra builds on

| Technique | What it compares | What Diffra keeps | What Diffra changes |
| --- | --- | --- | --- |
| Golden-file and snapshot testing | Output against a stored copy, usually as text | A stored reference and a reviewed update | Typed comparison instead of text diff; the reference is trusted by process, not by being in the branch |
| Approval testing | Output against an approved copy; a person approves changes | Human approval of changes | Approval is a recorded decision bound to exact evidence, not a renamed file |
| Characterization testing | Legacy behaviour, recorded as found | Recording behaviour without asserting it is right | The record is versioned evidence with its producer, toolchain and methodology |
| Differential testing | Two implementations, or two versions, on the same input | Comparison against a reference instead of an oracle | The comparison result (Delta) is separate from the verdict (Assessment) |
| Metric budgets and ratchets | A number against a threshold | Thresholds on growth | Thresholds apply to the change, under a named and versioned policy |

## What Diffra adds

### Typed evidence, not text

A snapshot test usually compares text. A reordered JSON object, a changed timestamp or a trailing
newline shows up as a difference, so teams add scrubbers until the test is quiet. Diffra compares
typed observations instead. An Evidence document declares a type and version, a scope and a
methodology. Two observations are compared only if all of these match; otherwise the result is
`incompatible`, not a pass. Reordering a keyed collection is not a change; reordering an ordered
sequence is. A new or removed metric is an explicit change, never a silent zero. See the
[protocol](protocol.md) for the full rules.

### A fact kept apart from a verdict

Most snapshot tools fuse two questions: *did the output change?* and *is the change acceptable?*
Diffra keeps them in separate documents. A Delta records what changed and contains no pass or
fail. An Assessment applies a named, versioned policy to one Delta. Changing the policy creates a
new Assessment and leaves the Delta and the Evidence untouched, so the same measured change can be
judged under a stricter rule later without measuring again.

### A baseline the candidate cannot rewrite

The failure mode of snapshot testing is the update command. When a snapshot fails, the quickest
fix is to regenerate it in the same pull request, and a reviewer sees a large file change they are
unlikely to read. The test then passes because the candidate approved itself.

Diffra separates producing evidence from trusting it. A candidate can propose new evidence but
cannot mark it trusted. A trusted baseline is pinned to an exact commit and evidence identity, and
the trust decision comes from a process outside the candidate pull request. The promotion rules in
[trust and identity](trust-and-identity.md) also require a second clean run at the same commit to
reproduce the same evidence identity before approval. That is a determinism check: evidence that
differs between two runs of the same code is not a baseline.

### Unknown is not a pass

A test runner reports pass or fail. Diffra has explicit states for the cases in between: evidence
that could not be produced, inputs that failed validation, comparisons the host cannot perform, and
comparisons that could not reach a result. None of them can produce a passing Assessment. A gate
built on Diffra cannot pass because a producer crashed and wrote nothing.

## What Diffra does not replace

Diffra detects and judges *change*. It does not decide whether the baseline was correct in the
first place. Keep the tests that state what correct means: example and property-based tests,
compilation checks, golden-file tests that an adopter already gates on. The
[Parquet.SourceGenerator adopter plan](adopters/parquet-source-generator.md) keeps those native
gates required and adds Diffra beside them.

Differential testing has a known weakness: if the baseline and candidate share a defect, comparing
them cannot reveal it. Diffra makes the baseline auditable (its commit, producer, toolchain and
approval are recorded) but cannot make it right.

## Where it fits in a pipeline

1. A producer observes the candidate commit and writes Evidence (`diffra collect`).
2. The baseline for the comparison is resolved to an exact commit and Evidence identity.
3. `diffra diff` writes a policy-free Delta.
4. `diffra eval` applies a policy and writes an Assessment; its exit code gates the build.
5. `diffra report` or `diffra present` renders the result for review.
6. After merge, trusted automation can promote the new evidence to become the next baseline.

Steps 1, 3, 4 and 5 are CLI commands today. Steps 2 and 6 exist as library APIs; see the
[feature status](README.md#feature-status).

## Further reading

- William McKeeman, "Differential Testing for Software", *Digital Technical Journal* 10(1), 1998.
- Michael Feathers, *Working Effectively with Legacy Code* (2004), on characterization tests.
- [ApprovalTests](https://approvaltests.com/) and [Verify](https://github.com/VerifyTests/Verify) for approval and snapshot testing in .NET.
- [RFC 8785](https://www.rfc-editor.org/rfc/rfc8785), the JSON Canonicalization Scheme behind Diffra identities.
