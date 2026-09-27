# ADR 0004: Policy status vocabulary

- Status: accepted
- Date: 2026-09-27

## Context

Comparison describes change. Policy decides whether a valid change is acceptable. Missing evidence and failed validation are not ordinary policy outcomes.

## Decision

Evidence status is `produced`, `unavailable`, `invalid`, or `inconclusive`. Delta status is `unchanged`, `changed`, `unavailable`, `invalid`, `incompatible`, `inconclusive`, or `unsupported`. Assessment outcome is `pass`, `warn`, `fail`, `needs-review`, or `inconclusive`. Findings use the same outcome vocabulary and cite Delta changes. Assessment cannot convert invalid, unavailable, incompatible, unsupported, or inconclusive input into a pass. Invalid Assessment input is a validation error, not an outcome. A report without Assessment contains no policy verdict.

## Consequences

Consumers can distinguish a clean comparison from missing or incompatible input. Policy changes produce a new Assessment from the same Delta; they do not recollect evidence or recompute comparison.
