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
2. **Changes to Diffra's own inputs are surfaced.** When a pull request changes the project file
   or Diffra's version pin, the output says so above other findings.
3. **Untrusted runs still get output.** Runs with a read-only token, such as pull requests from
   forks or dependency bots, write the result to the run's summary page. A separate posting
   workflow is optional and not part of the standard install.
4. **Approval and promotion are not part of the open tool.** A person or an agent decides whether
   to proceed with the findings.
5. **Restricting who may edit the project file is the repository's business** (code owners,
   branch protection), not Diffra's.

## Not yet decided

- The name and values of the enforcement setting.
- Whether a change to the scripts that collect the data is also surfaced, and how it is detected.
- The fate of the promotion code and of the Baseline reference's trust fields.

## Current code

`eval` exits 3 for `fail`, `needs-review` or `inconclusive`, and the self-evaluation job fails the
build on those. The Baseline reference carries `trusted`, `candidate` and `untrusted` statuses
and an `approved-main` role, and `Promotion` implements two-run promotion as library code.
