# 0001: Mission and scope

- Status: accepted
- Date: 2026-11-11

## Context

Diffra started as a typed comparison protocol built around trusted baselines, approval and
content-addressed identity. In practice the need is different. CI already produces many
signals (test results, lint findings, coverage, benchmarks, sizes, timings and files), each in
its own format, and a person or an agent reviewing a pull request has to assemble them by hand
to see what the change did.

## Decision

1. **Diffra is a tool first.** It reads the signals a repository's CI already produces, keeps
   them over time, and shows what a pull request changed. The typed documents and the protocol
   are the means, not the product.
2. **People and agents are both first-class readers.** Every run writes HTML for people and
   Markdown for agents. Neither is a fallback for the other.
3. **Surface, do not enforce.** Diffra reports what changed and how much attention it needs. The
   CI system and the repository's settings decide what blocks a merge. Failing the build is an
   opt-in setting ([0005](0005-surface-do-not-enforce.md)).
4. **Scope is any artifact a CI job produces**, including binary files, which are recorded with
   a hash of their bytes ([0004](0004-records-integrity-and-storage.md)).
5. **GitHub first, not GitHub only.** The core takes what it can from git itself. CI providers
   are integrations beside the core ([0007](0007-packaging-providers-and-the-action.md)).
6. **The open tool's line is "needs a service".** Everything a repository can compute from its
   own CI storage belongs in this repository, including rules over the history window.
   Approval, promotion and other workflow management are not part of the open tool.
7. **Diffra uses Diffra.** This repository installs Diffra the same way an adopter does: the same
   root file, the same Action and collecting job, inside the same wiring limits. Capabilities no
   adopter uses yet run in a separate lane that never gates this repository's CI.
8. **One repository.** The CLI, the protocol library, the converters, the GitHub Action and the
   reusable workflow all live here. Separate packages may be built from it.
9. **Alpha output carries no compatibility promise.** Any `0.x` document, file or report format
   may change between versions.

## Not yet decided

- Whether visual comparison of images is part of the first rollout.
- Whether the root `VISION.md` stays as a historical document.

## Current code

The CLI exposes separate `collect`, `diff`, `eval` and `report` operations over Evidence, Delta
and Assessment documents and a Baseline reference. This repository evaluates itself through
custom scripts (`scripts/RunDogfood.cs` and others) rather than an adopter's install. The later
records describe what replaces each part.
