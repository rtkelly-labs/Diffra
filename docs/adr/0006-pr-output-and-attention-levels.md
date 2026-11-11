# 0006: Pull request output and attention levels

- Status: accepted
- Date: 2026-11-11

## Context

Generated pull request comments fail when they show repository state as if it were change,
repeat themselves, print empty sections, and bury the one relevant fact. A reader, human or
agent, needs to know at once whether to act, and must be able to fetch everything else on
request.

## Decision

1. **Attention levels.** Every result has one of three levels: `act`, `review` or `quiet`, shown
   to people as "Needs action", "Worth reviewing" and "Quiet". The level is derived from rule
   outcomes only; it carries no size or magnitude. A failed rule gives `act`. A warning, a value
   that needs review, a failed attribution or an inconclusive comparison gives `review`, because
   an input that could not be judged is never shown as a pass (decision 9). Otherwise the level
   is `quiet`. A result takes the highest level of its findings. A change that no rule judged is
   `review`.
2. **One comment per pull request**, headed by the attention level. Each time a workflow
   finishes, the comment is rebuilt from every record present for the commit, never patched, and
   shows which datasets are still running.
3. **Nothing is posted on a quiet pull request.** Runs with a read-only token write to the run's
   summary page instead.
4. **Every run leaves the same traces.** It uploads the result under a fixed artifact name, and
   adds one line to the pull request's checks list stating the attention level and the number of
   datasets compared, linking to the summary page. That line is neutral or successful unless the
   adopter has turned on enforcement ([0005](0005-surface-do-not-enforce.md)).
5. **Only findings caused by the pull request** appear in the comment. Repository state goes to
   the full report.
6. **One screen.** The comment is limited by settings with these defaults: 25 rendered lines
   outside collapsed sections, 5 findings, tables of at most 4 columns, and about 500 tokens for
   the agent-facing Markdown. Anything more becomes one overflow line, with the full list in a
   collapsed section and in the bundle.
7. **Ranking.** Findings are ordered by attention level, then rule severity, then relative change
   within a dataset. Each dataset with an `act` finding gets at least one line. Many similar
   findings collapse into one.
8. **Agents fetch the rest themselves.** The comment and the summary page carry, in a collapsed
   section, the exact command that downloads that run's result with the CI host's own CLI. The
   result bundle holds the agent Markdown as a plain file. `diffra show` reads a downloaded bundle
   offline. The Diffra CLI does not fetch from the CI host.
9. **Comparison, rule and outcome stay distinct.** Missing, invalid, incompatible and
   inconclusive inputs are never shown as a pass.

## Not yet decided

- Whether a missing required dataset or a rejected record gives `act`. Neither exists yet.
- The fixed artifact name (proposed: `diffra-result`).
- The first lines of the comment.
- What happens to an existing comment when a pull request becomes quiet.

## Current code

`report` renders one Delta and an optional Assessment as HTML, JSON or Markdown, and also in two
one-screen formats. `--format comment` writes the pull request comment: the attention label, a
ranked table of at most 4 columns, and, past the limits, one overflow line and the full list in a
collapsed section. It is empty when the result is quiet. `--format agent` writes the
agent-facing Markdown: the attention token, the finding count and one line per finding within
about 500 tokens, ending with a truncation line when findings are left out. Without an Assessment
every change is worth reviewing, because no rule judged it. The limits are in `OutputLimits`
(`Attention.cs`), with the defaults above; the CLI does not set them yet. Ranking does not yet
group findings by dataset or collapse similar findings, nothing posts the comment, and there is
no checks-list line. The self-evaluation job writes the Markdown report to the job summary.
