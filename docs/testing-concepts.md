# Diffra in testing terms

Diffra is a form of **differential testing**: it judges a change by comparing what the software
produces for a pull request with what it produced at the commit the pull request branched from.
This page places Diffra among the testing techniques it borrows from, and says what it adds to them.

## The oracle problem

Every test needs an oracle: something that says whether an output is right. For many outputs worth
checking there is no practical oracle. Nobody can state in advance the correct generated source for
a schema, the right public API surface of a library, or the acceptable cyclomatic complexity of a
module. What a team *can* say is whether a change to one of these was intended.

Differential testing answers that by changing the question. Instead of "is this output correct?",
it asks "how does this output differ from a reference I already trust?" The reference can be
another implementation of the same specification (McKeeman's original use, for C compilers), the
same program on different inputs that should agree, or the same program at an earlier commit.
Diffra uses the last: the reference is the record stored for the commit the pull request branched
from, or for the target branch tip when CI tests the merged result. That commit is the comparison
point ([0003](adr/0003-comparison-history-and-noise.md)).

## Techniques Diffra builds on

| Technique | What it compares | What Diffra keeps | What Diffra changes |
| --- | --- | --- | --- |
| Golden-file and snapshot testing | Output against a stored copy, usually as text | A stored reference | Typed comparison instead of text diff; the reference is the record CI stored for the comparison point, not a file in the branch |
| Approval testing | Output against an approved copy; a person approves changes | A person deciding whether a change was intended | No approval step: the change is shown in the pull request and the reviewer decides ([0005](adr/0005-surface-do-not-enforce.md)) |
| Characterization testing | Legacy behaviour, recorded as found | Recording behaviour without asserting it is right | The record is versioned evidence with its producer, toolchain and methodology |
| Differential testing | Two implementations, or two versions, on the same input | Comparison against a reference instead of an oracle | The comparison result (Delta) is separate from the verdict (Assessment) |
| Metric budgets and ratchets | A number against a threshold | Thresholds on growth | Thresholds apply to the change, under a named rule set; noisy measures are judged against recent history |

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

### No reference in the branch to regenerate

The failure mode of snapshot testing is the update command. When a snapshot fails, the quickest
fix is to regenerate it in the same pull request, and a reviewer sees a large file change they are
unlikely to read. The test then passes because the candidate approved itself.

Diffra has no stored copy in the branch, so there is nothing to regenerate in the diff. The
reference is the record CI stored for the comparison point, a commit already on the target branch.
When the commit a pull request branched from has no record,
Diffra uses the nearest earlier one in its history window and states how far back it is; when
none has a record, it says it cannot attribute the change. There is no approval or promotion
step.

### Unknown is not a pass

A test runner reports pass or fail. Diffra has explicit states for the cases in between: evidence
that could not be produced, inputs that failed validation, comparisons Diffra cannot perform, and
comparisons that could not reach a result. None of them can produce a passing Assessment, so a
step that enforces the result cannot pass because a producer crashed and wrote nothing.

## What Diffra does not replace

Diffra detects and judges *change*. It does not decide whether the reference was correct in the
first place. Keep the tests that state what correct means: example and property-based tests,
compilation checks, golden-file tests that an adopter already gates on. The
[Parquet.SourceGenerator adopter plan](adopters/parquet-source-generator.md) keeps those native
gates required and adds Diffra beside them.

Differential testing has a known weakness: if the reference and candidate share a defect, comparing
them cannot reveal it. Diffra makes the reference auditable (its commit, producer and toolchain are
recorded) but cannot make it right.

## Where it fits in a pipeline

Today the steps are separate CLI operations from the earlier design:

1. A producer observes the candidate commit and writes Evidence (`diffra collect`).
2. The comparison point is found: the record for the commit the pull request branched from, or
   the nearest earlier one in the history window.
3. `diffra diff` writes a policy-free Delta.
4. `diffra eval` applies rules and writes an Assessment. Its exit code is non-zero for any outcome
   other than pass or warn; under [0005](adr/0005-surface-do-not-enforce.md), failing the build becomes the
   opt-in setting `fail-on`.
5. `diffra report` or `diffra present` renders the result for review.

Steps 1, 3, 4 and 5 are CLI commands. Step 2 is done by the self-evaluation script; see the
[feature status](README.md#feature-status). In the design set by the
[decision records](adr/), each job that produces data runs one `diffra record` command and a
collecting job gathers the records for the commit
([0002](adr/0002-project-file-and-datasets.md), [0004](adr/0004-records-integrity-and-storage.md)).

## Further reading

- William McKeeman, "Differential Testing for Software", *Digital Technical Journal* 10(1), 1998.
- Michael Feathers, *Working Effectively with Legacy Code* (2004), on characterization tests.
- [ApprovalTests](https://approvaltests.com/) and [Verify](https://github.com/VerifyTests/Verify) for approval and snapshot testing in .NET.
- [RFC 8785](https://www.rfc-editor.org/rfc/rfc8785), the JSON Canonicalization Scheme behind Diffra identities.
