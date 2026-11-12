# Diffra vision

CI produces many signals: test results, lint findings, coverage, benchmark tables, package
sizes, CI timing, and files such as reports and images. Each tool writes its own format, so a
reader who wants to know what a pull request changed must collect the output, compare it with
earlier runs by hand, and pick out what matters. Diffra began as hand-written Markdown summaries
given to an agent to steer a pull request that was going wrong.

Diffra is a tool that does this work. It reads the output of the tools a repository already
uses, records it as typed datasets for each commit, and compares a pull request with the commit
it branched from and with recent history. It shows what changed in one short summary that people
and agents can both read. It works with any tooling stack and starts with GitHub. Its record
formats, schemas and comparison rules are the means, not the product.

This page is kept true: it changes in the same pull request as any change to scope, behaviour or
a decision, so it describes what Diffra is now rather than what it was once planned to be. The
words used here are defined in the [glossary](glossary.md). The [decision records](adr/) set out
the design, and the [plan](../PLAN.md) gives the order of work.

## Surface, do not enforce

The point of Diffra is to surface problems. It is not a tool for managing workflow. Whether a
pull request can merge is CI's business, and the adopter decides it. By default Diffra's step
never fails a build; an adopter who wants that sets the `fail-on` input of Diffra's step to
`fail` or `warn`, and uses CI's own settings to decide which jobs block a merge. Diffra has no
approval step, no promotion of baselines and no trusted reference: a pull request is compared
with the record stored for the commit it branched from on the target branch. When that commit
has no record, Diffra uses the nearest earlier one in its history window and says how far back
it is; when none has a record, it says it cannot attribute the change rather than guessing.
There is no comparison with the previous release for now.

## What a reader sees

Each pull request gets at most one comment, headed by one attention level: `act`, `review` or
`quiet`. The level comes from rule findings, never from the size of a change. The comment fits
on one screen and lists only what the pull request caused; the rest of the repository's state
goes to the full report. A quiet pull request gets no comment. Every run leaves the same traces:
a result uploaded as the artifact `diffra-result`, one line in the checks list, and a collapsed
command that an agent can run with the CI host's own CLI to fetch the full result. A person
reads the HTML report; an agent reads the Markdown. Both are produced together. An optional
dataset that was never collected in the history window gets one line in the full report and none
in the comment.

## How it fits into CI

One root file in the repository defines the datasets, the rule sets on each dataset, and the
projects that select them. Each CI job that produces data runs one command, `diffra record`,
which reads the tool's output through the converter the root file declares, or takes a single
number with `--value`. A value that does not fit the root file is saved as rejected and reported
at the end rather than failing the job. One collecting job in each workflow file gathers the
records for the commit; the root file is to say which workflow file is responsible for which
datasets, so that a workflow does not report another's datasets as missing. A required dataset
that is missing or only rejected counts as a warning, so it fails the step under `fail-on: warn`
and not under `fail-on: fail`. One whose collector reported that it failed is a failure of the
build, so it counts as `fail` and fails the step under either setting.

Diffra is to read the formats tools already write: test results, coverage, lint findings, sizes,
tables such as benchmark results and OpenMetrics, with declared columns, and flame graphs as
collapsed stacks. Readers that need a large library, such as Parquet, ship as separate packages
outside the core. History is to be stored in a branch of the repository by default, one record
per commit, and the adopter can choose another store. On GitHub, provider work starts as Action
steps and moves into a small companion, `diffra-github`, beside the one core CLI when it
outgrows them. CI already schedules jobs; Diffra records the job graph and its timing as data
and does not run work itself. Measures that are noisy from run to run are judged against a
window of recent history rather than one earlier value.

## Diffra uses Diffra

Diffra installs itself the way an adopter does: the same root file, the same Action and
collecting job, within the same wiring limits. Capabilities that no adopter has yet, such as
visual comparison, run in a separate lane that never fails the build. The converters are
benchmarked here on real input. Until the root file and the Action exist, this repository's own
evaluation uses the scripts in `scripts/`; they are retired when the adopter's path can replace
them.

## Who it is for first

Diffra is first for its owner's own repositories, adopted one at a time so that each one tests a
different language and set of signals. The [plan](../PLAN.md) names the repositories, their
order, and the conditions that pause the rollout. After that, one installation in a repository
the owner does not own tests whether the setup works for someone else.

## What exists today

Most of the above is direction, not code. The current .NET CLI accepts a bounded structured
numeric payload. `collect` creates Evidence, `diff` records a Delta against a supplied baseline,
`eval` creates an Assessment under separate rules, and `report` renders the result as HTML, JSON
or Markdown, and as the one-screen comment and agent formats headed by an attention level.
Nothing posts the comment yet. `bundle` moves evidence through a content-addressed store, and
`present` builds and serves a review bundle. The root file has a schema and a loader that checks
its cross-references, which `diffra record` uses; `diffra merge` reads the `fail-on`
setting and fails the step on a required dataset not collected when it is `warn`, and on one
recorded only as failed when it is `fail` or `warn`; and `diffra record` records a number given with `--value`, or a skipped or failed dataset,
checked against the root file, and `diffra merge` merges the entries files of every job,
reporting every disagreement in values or statuses, naming each job involved, and listing the
planned datasets no job collected. Nothing yet carries the jobs' files to a collecting job or
writes the merged entries to history, and no converter for a tool's own format exists, so
`record` cannot yet read the files a dataset's `from` names. The names Evidence, Delta and Assessment come from the earlier design; the
glossary gives the words that replace them. The [sample
workflow](../examples/sample-project/workflow/) contains inputs and checked-in outputs, the
[protocol](protocol.md) describes the current document shapes, and the [CLI reference](cli.md)
the commands. The [feature status](README.md#feature-status) table shows what is a command, what
is a library API only, and what is planned.

The root [VISION.md](../VISION.md) is the original design handover. It describes an earlier
design built around a trusted baseline and approval, and is kept as history.
