# Diffra vision

CI produces many signals: test results, lint findings, coverage, benchmark tables, package
sizes, CI timing, and files such as reports and images. Each tool writes its own format, so a
reader who wants to know what a pull request changed must collect the output, compare it with
earlier runs by hand, and pick out what matters. Diffra began as hand-written Markdown summaries
given to an agent to steer a pull request that was going wrong.

Diffra is a tool that does this work. It reads the output of the tools a repository already
uses, records it as typed datasets for each commit, and compares a pull request with the commit
it branched from and with recent history. It shows what changed in one short summary that
people and agents can both read. It works with any tooling stack and starts with GitHub. Its
record formats, schemas and comparison rules are the means, not the product.

The words used here are defined in the [glossary](glossary.md). The
[decision records](adr/) set out the design, and the [plan](../PLAN.md) gives the order of work.

## Surface, do not enforce

The point of Diffra is to surface problems. It is not a tool for managing workflow. Whether a
pull request can merge is CI's business, and the adopter decides it. By default Diffra's step
never fails a build; an adopter who wants that sets the `fail-on` input of Diffra's step, and
uses CI's own settings to decide which jobs block a merge. Diffra has no approval step, no promotion of
baselines and no trusted reference: a pull request is compared with the record stored for the
commit it branched from on the target branch. When that commit has no record, Diffra uses the
nearest earlier one in its history window and says how far back it is; when none has a record,
it says it cannot attribute the change rather than guessing.

## What a reader sees

Each pull request gets at most one comment, headed by one attention level: `act`, `review` or
`quiet`. The level comes from rule findings, never from the size of a change. The comment fits
on one screen and lists only what the pull request caused; the rest of the repository's state
goes to the full report. A quiet pull request gets no comment. Every run leaves the same traces:
a result uploaded under a fixed name, one line in the checks list, and a collapsed command that
an agent can run with the CI host's own CLI to fetch the full result. A person reads the HTML
report; an agent reads the Markdown. Both are produced together.

## How it fits into CI

One root file in the repository defines the datasets, the rule sets on each dataset, and the
projects that select them. Each CI job that produces data runs one command, `diffra record`,
which reads the tool's output through a declared converter. A collecting job in each workflow
file gathers the records for the commit. CI already schedules jobs; Diffra records the job
graph and its timing as data and does not run work itself. Measures that are noisy from run to
run are judged against a window of recent history rather than one earlier value.

## Diffra uses Diffra

Diffra installs itself the way an adopter does: the same root file, the same Action and
collecting job, within the same wiring limits. Capabilities that no adopter has yet, such as
visual comparison, run in a separate lane that never fails the build. The
converters are benchmarked here on real input. Until the root file and the Action exist, this
repository's own evaluation uses the scripts in `scripts/`; they are retired when the adopter's
path can replace them.

## Who it is for first

Diffra is first for its owner's own repositories, adopted one at a time so that each one tests
a different language and set of signals. The [plan](../PLAN.md) names the repositories, their
order, and the conditions that pause the rollout. After that, one installation in a repository
the owner does not own tests whether the setup works for someone else.

## What exists today

Most of the above is direction, not code. The current .NET CLI accepts a bounded structured
numeric payload. `collect` creates Evidence, `diff` records a Delta against a supplied baseline,
`eval` creates an Assessment under separate rules, and `report` renders the result as HTML, JSON
or Markdown. `bundle` moves evidence through a content-addressed store, and `present` builds and
serves a review bundle. These names come from the earlier design; the glossary gives the words
that replace them. The [sample workflow](../examples/sample-project/workflow/) contains inputs
and checked-in outputs, the [protocol](protocol.md) describes the current document shapes, and
the [CLI reference](cli.md) the commands. The [feature status](README.md#feature-status) table
shows what is a command, what is a library API only, and what is planned.

The root [VISION.md](../VISION.md) is the original design handover. It describes an earlier
design built around a trusted baseline and approval, and is kept as history.
