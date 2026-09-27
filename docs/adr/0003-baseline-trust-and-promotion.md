# ADR 0003: Baseline trust and promotion

- Status: accepted
- Date: 2026-09-27

## Context

Candidate code must not make its own output the trusted reference used to approve that output. Branch names move, and regenerated snapshots can hide regressions if they replace the baseline without review.

## Decision

Resolve every baseline selector to an immutable revision, manifest digest, and Evidence ID. Separate candidate creation from promotion. Promotion requires approval tied to the candidate ID and a clean second production run at the same subject commit with matching Evidence ID and artifact digests. Record the trust decision and invalidate promotion when relevant identities change. A candidate producer cannot assert trusted status.

## Consequences

Missing, corrupt, stale, or unapproved baselines cannot yield a passing gate. Baseline status uses the wire values `trusted`, `candidate`, `untrusted`, `missing`, `corrupt`, or `incompatible`. The first implementation can use a local trust decision, but the reference format leaves room for signed CI/reviewer approval without changing Evidence identity.
