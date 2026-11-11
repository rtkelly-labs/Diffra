# 0005: Surface, do not enforce

- Status: accepted
- Date: 2026-11-11

## Context

The earlier design made Diffra a gate: policy was read from a trusted copy, approval had to come
from outside the pull request, and a candidate could never mark its own reference trusted. In
practice the code a pull request changes also measures the values, the likely editor of a budget
is an agent asked to make CI pass, and branch protection already decides what blocks a merge.

## Decision

1. **Diffra surfaces; CI decides.** By default the final step does not fail the build. Failing on
   findings is a setting the adopter turns on.
2. **The setting is `fail-on: never | fail | warn`, default `never`.** It names the lowest finding
   status that fails the step: `fail` fails it on any finding with the status `fail`, and `warn` on
   any finding with the status `fail` or `warn`.
3. **Changes to Diffra's own inputs are surfaced.** When a pull request changes the project file
   or Diffra's version pin, the output says so above other findings.
4. **Untrusted runs still get output.** Runs with a read-only token, such as pull requests from
   forks or dependency bots, write the result to the run's summary page. A separate posting
   workflow is optional and not part of the standard install.
5. **Approval and promotion are not part of the open tool.** A person or an agent decides whether
   to proceed with the findings.
6. **Restricting who may edit the project file is the repository's business** (code owners,
   branch protection), not Diffra's.

## Not yet decided

- Whether the statuses `needs-review`, `inconclusive` and `changed` count towards `fail-on`, and
  whether a required dataset that was not collected, a rejected entry, a first run with no history
  and a comparison that cannot be attributed do. Until this is ruled, none of them fails the step;
  only the statuses `fail` and `warn` do. `eval` today treats `needs-review` and `inconclusive` as
  more severe than `warn`.
- Where `fail-on` is set. The proposal is an input of Diffra's step in the CI wiring rather than a
  root-file setting, following "a CI option". A value other than the three is proposed to be an
  error rather than the default, so a misspelt setting does not silently turn enforcement off.
- Whether a change to the scripts that collect the data is also surfaced, and how it is detected.
- The fate of the promotion code and of the Baseline reference's trust fields.

## Current code

`eval` exits 3 for `fail`, `needs-review` or `inconclusive`, and the self-evaluation job fails the
build on those. `Enforcement` in the protocol library reads `fail-on` and decides whether findings
fail the step. Nothing calls it yet; it takes effect when the final step that collects the records
exists. The Baseline reference carries `trusted`, `candidate` and `untrusted` statuses
and an `approved-main` role, and `Promotion` implements two-run promotion as library code.
