# 0003: Comparison point, history window and noise

- Status: accepted
- Date: 2026-11-11

## Context

A pull request's values are only useful beside what they replaced. The earlier design compared
against an approved Baseline reference and, in the self-evaluation script, fell back to any of
the last 20 commits with data without stating the distance. A pull request was then blamed for
changes other pull requests had made. Noisy values such as timings cannot be judged by a fixed threshold.

## Decision

1. **There is no baseline document, approval or promotion.** A pull request is compared with a
   record from its target branch.
2. **The comparison point is one fixed rule, not a setting.** When CI tests the pull request
   merged into its target branch, compare with the target branch tip; otherwise compare with the
   merge base. If that commit has no record, use the nearest recorded ancestor inside the history
   window and state the distance. The point is found by commit ancestry only. No branch is named
   as primary.
3. **Cannot attribute.** When nothing in the window qualifies, the output says so in one line
   and does not compare.
4. **The history window is its own setting.** It is the set of recent records used for judging
   noise and for rules over time. Its only part in finding the comparison point is to bound how
   far back decision 2 looks. Gaps are shown, not filled (for example "history: 18 of 20
   commits"). History is rebuilt by re-reading stored records, never by measuring again.
5. **Noise.** Each converter marks every measure as deterministic or noisy.
   - Deterministic measures (counts, allocated bytes, sizes, test outcomes) use fixed thresholds
     against the comparison point from the first pull request.
   - Noisy measures (timings, CI duration, sampled data) are judged against the range of the
     history window. One run can raise them at most to `review`. They are shown, not judged,
     until the window holds enough records.
   - A shift that holds over several consecutive recorded commits on the target branch raises
     `act` there.
6. **Comparison is separate from rules.** A comparison records what changed and carries no
   verdict. Rules are evaluated afterwards, so a rule change does not need new measurements.
7. **Collections.** Arrays are ordered unless a type declares a collection keyed by a stable
   key. A keyed collection compares members by key and ignores input order; a changed key is one
   removal and one addition; a missing or duplicate key makes the value invalid.
8. **Branching models.** The rule must not assume trunk-based development. It is tested with
   fixture repositories for five history shapes: trunk with squash merges, trunk with merge
   commits, stacked pull requests, release branches, and a merge queue. A long-lived `develop`
   branch is expected to work but is not tested. Rebase merges and force-pushes leave records for
   commits no longer on the branch; those records are ignored.

## Not yet decided

- How many records a noisy measure needs before it is judged (proposed: 10).
- What happens when two merges finish at nearly the same time, and when stored CI artifacts
  expire.

## Current code

`diff` takes an explicit Baseline reference and baseline Evidence. `GitBaselineResolver` exists
as library code; the two-run promotion code has been removed. `scripts/RunDogfood.cs` compares with the merge base, or with
the nearest recorded first-parent ancestor in a fixed window of 20 commits ending at the merge
base, and states the distance and the commits in between. With no record in the window it
reports that it cannot attribute the change. The window is not yet a setting. Keyed collection
comparison exists in `KeyedCollectionComparator`.
