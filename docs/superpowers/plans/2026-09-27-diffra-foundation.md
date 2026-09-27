# Diffra foundation implementation plan

> **For agentic workers:** Use the local `PLAN.md` and `VISION.md` as the specification. Complete each checked task with a build or fixture check before moving on.

**Goal:** Establish an independent, buildable Diffra repository and prove the Evidence → Delta → Assessment → report separation with a small local fixture.

**Architecture:** `Diffra.Protocol` owns immutable portable documents and deterministic identities. `Diffra.Cli` composes explicit operations over those documents. Schemas and examples define the wire boundary for non-.NET producers. Baseline trust and later extensions remain separate from evidence generation.

**Tech stack:** .NET 10, `System.Text.Json`, JSON Schema draft 2020-12, GitHub Actions.

**Spec:** [`VISION.md`](../../../VISION.md), [`PLAN.md`](../../../PLAN.md).

## Global constraints

- The repository is independent of Aspire and Parquet internals.
- Do not put a policy verdict into Evidence or Delta.
- Unknown type/version and missing or untrusted baselines never produce a pass.
- Preserve raw artifact digests separately from semantic evidence identity.
- Keep external producers bounded and treat their output as untrusted.
- Keep source, comparison, assessment, and presentation identities separate.
- Do not publish packages or create GitHub issues as part of this plan.

## Tasks

### Task 1: Repository and wire contract

**Files:** `README.md`, `AGENTS.md`, `global.json`, `Diffra.slnx`, `src/Protocol/Diffra.Protocol.csproj`, `src/Cli/Diffra.Cli.csproj`, `.github/workflows/ci.yml`, `schemas/*.schema.json`, `docs/protocol.md`, `docs/trust-and-identity.md`, `docs/adr/*.md`.

- [x] Copy the handover into the independent repository and pin the SDK.
- [x] Build the solution with `dotnet build Diffra.slnx -c Release`.
- [x] Define the required document fields and reject malformed fixtures with a JSON Schema validator.
- [x] Record canonical identity, version compatibility, baseline trust, policy vocabulary, and collection identity decisions.
- [x] Confirm the CI workflow runs the same build and test commands with SHA-pinned actions.

### Task 2: Semantic evidence identity

**Files:** `src/Protocol/CanonicalJson.cs`, `src/Protocol/EvidenceIdentity.cs`, `tests/Protocol.Tests/IdentityTests.cs`.

- [x] Add fixtures whose JSON objects have different property order but the same semantic value.
- [x] Make both fixtures produce the same SHA-256 evidence ID; a changed meaningful value must produce a different ID.
- [x] Reject duplicate JSON keys, path traversal, oversized input and mismatched raw artifact digests.
- [x] Run `dotnet run --project tests/Protocol.Tests/Protocol.Tests.csproj -c Release` and confirm the identity checks execute.

### Task 3: Independent comparison and assessment

**Files:** `src/Protocol/Comparison.cs`, `src/Protocol/Assessment.cs`, `tests/Protocol.Tests/ComparisonTests.cs`.

- [x] Compare only matching evidence type and version or an explicitly registered migration.
- [x] Report a formatting-only JSON change as unchanged, keyed reorder as unchanged, and ordered reorder as changed.
- [x] Keep Delta free of a policy verdict. Evaluate the same Delta with two different rule sets and verify only Assessment identity changes.
- [x] Run `dotnet run --project tests/Protocol.Tests/Protocol.Tests.csproj -c Release` and confirm the comparison checks execute.

### Task 4: CLI and review proof

**Files:** `src/Cli/Program.cs`, `src/Protocol/Report.cs`, `tests/Integration.Tests/WorkflowTests.cs`, `examples/sample-project/*`.

- [x] Implement `collect`, `diff`, `eval`, and `report` as separate file-to-file steps with clear nonzero exits for invalid evidence and failed policy.
- [x] Run the sample fixture through all four steps and check that the report cites exact baseline/candidate, comparator and policy identities.
- [x] Render identical inputs twice and compare report bytes; ensure evidence text is escaped in HTML.
- [x] Run `dotnet run --project tests/Protocol.Tests/Protocol.Tests.csproj -c Release`.

## Later milestones

`PLAN.md` phases 2–5 cover graph execution, external command adapter, Git merge-base trust, local content-addressed bundles, candidate verification, full report kit and the Parquet.SourceGenerator adopter. Each phase must retain the four-document separation proven here.
