# Diffra Agent Continuation Handover

**Date**: 2026-09-28  
**Repository**: `/Users/ryankelly/code/personal/Diffra`  
**GitHub**: `rtkelly-labs/Diffra`  
**Current Branch**: `feature/graph-host`  
**Base Branch for PR #20**: `feature/cas-bundles`  
**Primary Language/Runtime**: .NET 10 (`10.0.401` pinned in `global.json`, C# 13)

---

## 1. Executive Summary & Core Invariants

Diffra is an independent .NET repository for **typed evidence, trusted baselines, and reviewable change**. It provides an immutable, content-addressed protocol and CLI for capturing software observations (metrics, generated code, API surfaces, coverage, complexity), calculating typed policy-free deltas against trusted baselines, evaluating those deltas under versioned policies, and presenting self-contained review bundles.

### Critical Estate Invariants
1. **Zero Aspire Runtime Dependencies**: Diffra has **zero** compile-time or runtime dependencies on Aspire (estate invariant). CI enforces this with automated grep gating.
2. **Zero Warnings Policy**: `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` is pinned across all projects in `Directory.Build.props`. The build must always report 0 warnings and 0 errors.
3. **No Python Automation Policy**: Python (.py) scripts are strictly forbidden for tooling/automation. All automation, benchmarks, and scripts are authored in C# (.cs) executed via `dotnet run`.
4. **Stacked PR Workflow**: Changes are managed via clean stacked branches with squash-and-merge onto linear history. Never commit directly to `main`.
5. **Operating Principle: "Diffra Utilizes Diffra" (Self-Verification)**:
   Diffra is its own first and continuous client. Utilizing Diffra to build, test, baseline, evaluate, and review Diffra is a core operating principle. Every capability added to Diffra must immediately be incorporated into Diffra's own development, test, and CI gating workflow.

---

## 2. Active Stack on GitHub (`rtkelly-labs/Diffra`)

All previous milestones are stacked, reviewed, tested, and tracked on GitHub:

| PR / Branch | Target Base | Status | Description |
|---|---|---|---|
| **[PR #2](https://github.com/rtkelly-labs/Diffra/pull/2)** | `main` | Open (Green) | Phase 0: Protocol contracts, draft 2020-12 schemas, ADRs 0001–0005. |
| **[PR #3](https://github.com/rtkelly-labs/Diffra/pull/3)** | `feature/protocol-contracts` | Open (Green) | Phase 1.1: Canonical JSON identity, SHA-256 payload hashing (`Diffra.Protocol`). |
| **[PR #4](https://github.com/rtkelly-labs/Diffra/pull/4)** | `feature/protocol-library` | Open (Green) | Phase 1.2: File-to-file CLI commands (`collect`, `diff`, `eval`, `report`). |
| **[PR #5](https://github.com/rtkelly-labs/Diffra/pull/5)** | `feature/file-cli` | Open (Green) | Phase 1.3: Sample project workflow and end-to-end integration tests. |
| **[PR #1](https://github.com/rtkelly-labs/Diffra/pull/1)** | `feature/sample-workflow` | Open (Green) | Phase 1.4: Current vision, docs publication plan, artifact delivery design. |
| **[PR #12](https://github.com/rtkelly-labs/Diffra/pull/12)** | `feature/bootstrap` | Open (Green) | Phase 1.6 + CI: CAS, portable bundles, design system reporting, review server, dogfooding, pre-release package `Diffra.Cli.0.1.0-preview.1.nupkg`, ADRs 0006–0009. |
| **[PR #20](https://github.com/rtkelly-labs/Diffra/pull/20)** | `feature/cas-bundles` | Open (Green) | Phase 2: Graph composition host, DAG validation, cascading fault isolation, external command adapter ([Issue #9](https://github.com/rtkelly-labs/Diffra/issues/9)). |

---

## 3. What Has Been Completed & Where Code Lives

### Core Protocol (`src/Protocol/`)
- **Canonical JSON & Identity**: [`CanonicalJson.cs`](file:///Users/ryankelly/code/personal/Diffra/src/Protocol/CanonicalJson.cs), [`DocumentIdentity.cs`](file:///Users/ryankelly/code/personal/Diffra/src/Protocol/DocumentIdentity.cs), [`EvidenceIdentity.cs`](file:///Users/ryankelly/code/personal/Diffra/src/Protocol/EvidenceIdentity.cs).
- **Comparison & Assessment**: [`Comparison.cs`](file:///Users/ryankelly/code/personal/Diffra/src/Protocol/Comparison.cs), [`Assessment.cs`](file:///Users/ryankelly/code/personal/Diffra/src/Protocol/Assessment.cs).
- **Content-Addressed Store (CAS)**: [`ContentAddressedStore.cs`](file:///Users/ryankelly/code/personal/Diffra/src/Protocol/ContentAddressedStore.cs) (atomic writes, verified digests, corruption defense).
- **Portable Evidence Bundles**: [`EvidenceBundle.cs`](file:///Users/ryankelly/code/personal/Diffra/src/Protocol/EvidenceBundle.cs) (`.diffra.bundle` export/import with Zip-Slip defense).
- **Reporting & Design System**: [`Reporting.cs`](file:///Users/ryankelly/code/personal/Diffra/src/Protocol/Reporting.cs), [`DeliveryIndex.cs`](file:///Users/ryankelly/code/personal/Diffra/src/Protocol/DeliveryIndex.cs), [`PresentationBundle.cs`](file:///Users/ryankelly/code/personal/Diffra/src/Protocol/PresentationBundle.cs) (styled via `@rtkelly13/design-system` tokens, `sketch`/`midnight` themes, verdict banner, stat cards).
- **Standardized Code Coverage**: [`Coverage.cs`](file:///Users/ryankelly/code/personal/Diffra/src/Protocol/Coverage.cs) ([ADR 0006](file:///Users/ryankelly/code/personal/Diffra/docs/adr/0006-standardized-code-coverage-evidence.md), [`schemas/coverage-v1.schema.json`](file:///Users/ryankelly/code/personal/Diffra/schemas/coverage-v1.schema.json)) — relative path normalization, interval encoding (`"14-16, 25"`), newly-uncovered line detection against Git diff hunks, zero-regression policy.
- **Time-Series Longitudinal Graphing**: [`TimeSeriesGraph.cs`](file:///Users/ryankelly/code/personal/Diffra/src/Protocol/TimeSeriesGraph.cs) ([ADR 0007](file:///Users/ryankelly/code/personal/Diffra/docs/adr/0007-time-series-projections-and-evolution-graphing.md), [`schemas/timeseries-v1.schema.json`](file:///Users/ryankelly/code/personal/Diffra/schemas/timeseries-v1.schema.json)) — ASCII sparklines and offline-first Design System SVG evolution charts with zero runtime JS (strict CSP compliant).
- **CNCF CloudEvents v1.0.2**: [`CloudEvents.cs`](file:///Users/ryankelly/code/personal/Diffra/src/Protocol/CloudEvents.cs) ([ADR 0009](file:///Users/ryankelly/code/personal/Diffra/docs/adr/0009-cloudevents-specification-and-event-driven-integration.md), [`schemas/cloudevent-diffra-v1.schema.json`](file:///Users/ryankelly/code/personal/Diffra/schemas/cloudevent-diffra-v1.schema.json)) — vendor-neutral event envelope, routing attributes (`diffraoutcome`, `diffragithash`, `traceparent`).
- **Graph Composition Host & Engine**: [`Graph.cs`](file:///Users/ryankelly/code/personal/Diffra/src/Protocol/Graph.cs), [`GraphValidator.cs`](file:///Users/ryankelly/code/personal/Diffra/src/Protocol/GraphValidator.cs), [`GraphExecutor.cs`](file:///Users/ryankelly/code/personal/Diffra/src/Protocol/GraphExecutor.cs) — code-first builder, Kahn's algorithm topological sorting, DFS cycle path tracing, missing input & type compatibility validation, cascading fault isolation (`Blocked`/`Inconclusive`).
- **External Command Adapter & Builder**: [`ExternalCommandAdapter.cs`](file:///Users/ryankelly/code/personal/Diffra/src/Protocol/ExternalCommandAdapter.cs), [`EvidenceDocumentBuilder.cs`](file:///Users/ryankelly/code/personal/Diffra/src/Protocol/EvidenceDocumentBuilder.cs) — safe process execution without shell interpolation, bounded buffer limits, cryptographic evidence parity with in-process producers.

### CLI (`src/Cli/`)
- `diffra collect`: Generates canonical Evidence from payload.
- `diffra diff`: Generates policy-free Delta between baseline and candidate.
- `diffra eval`: Evaluates Delta against versioned policy rules.
- `diffra report`: Renders HTML, JSON, or Markdown review summaries.
- `diffra bundle export` / `diffra bundle import`: CAS bundle portability.
- `diffra present bundle` / `diffra present serve`: Materializes presentation review bundles and serves on loopback via ASP.NET Core with strict CSP.

### Self-Dogfooding & Scripts (`scripts/`, `baselines/dogfood/`)
- [`scripts/DogfoodMetrics.cs`](file:///Users/ryankelly/code/personal/Diffra/scripts/DogfoodMetrics.cs): Collects architectural facts (zero Aspire deps, 0 compiler warnings, schema counts, source file counts).
- [`scripts/InitializeDogfoodBaseline.cs`](file:///Users/ryankelly/code/personal/Diffra/scripts/InitializeDogfoodBaseline.cs): Re-initializes baseline files in `baselines/dogfood/`.
- [`scripts/RunDogfood.cs`](file:///Users/ryankelly/code/personal/Diffra/scripts/RunDogfood.cs): Executes candidate collection, merge-base diff, quality evaluation, presentation bundle generation, and `$GITHUB_STEP_SUMMARY` reporting.

### CI & Safety Tooling (`.github/workflows/`)
- [`ci.yml`](file:///Users/ryankelly/code/personal/Diffra/.github/workflows/ci.yml): Enforces No Python policy, zero Aspire check, vulnerability audit (`dotnet list package --vulnerable`), test suite execution, dogfood pipeline execution, review bundle upload, and pre-release CLI packaging (`Diffra.Cli.0.1.0-preview.1.nupkg`).
- [`pr-title.yml`](file:///Users/ryankelly/code/personal/Diffra/.github/workflows/pr-title.yml): Enforces conventional commit titles via pinned semantic PR action.

---

## 4. Immediate Next Step: Phase 3 ([Issue #10](https://github.com/rtkelly-labs/Diffra/issues/10))

The next phase to implement is **Phase 3: Baselines, Git merge-base resolver, typed complexity, and promotion controls**:

### Scope of Phase 3
1. **Git Merge-Base Baseline Resolver**:
   - Resolve merge-base commit dynamically using Git (`git merge-base origin/main HEAD`).
   - Refuse mutable branch labels without resolving and recording the exact commit SHA and manifest digest.
   - Record baseline role (`merge-base`), resolver identity, source store, and trust status.
2. **Typed Cyclomatic Complexity Evidence Model**:
   - Model method identity, method count, total/mean complexity, quantiles, and outlier detection.
   - Record scope, methodology, and producer compatibility.
   - Implement comparator and policy to detect and warn/fail on outlier growth while displaying normalized values.
3. **Structured JSON & Keyed Collection Comparison**:
   - Retain text comparison for byte/text-oriented evidence, but implement typed numeric and keyed-collection comparison.
   - Reordering in keyed collections remains unchanged; reordering in ordered sequences registers a change.
4. **Candidate Verification, Approval Records, and Atomic Promotion Controls**:
   - Implement candidate and verified states for local promotion.
   - Require a clean second production run at the same head commit and digest before approval.
   - Persist approval record tied to evidence and policy identity; invalidate if relevant identities change.

### Branch Strategy for Phase 3
- Create branch off `feature/graph-host`:
  ```bash
  git checkout -b feature/baselines-and-trust
  ```
- Target base for PR: `feature/graph-host` (stacking on PR #20).

---

## 5. Verification Commands

Always execute these verification commands before committing or opening a PR:

```bash
# 1. Build solution in Release mode (verifies 0 warnings, 0 errors)
dotnet build Diffra.slnx -c Release

# 2. Run the complete test suite (25 suites passing)
dotnet run --project tests/Protocol.Tests/Protocol.Tests.csproj -c Release --no-build

# 3. Run Diffra self-dogfooding pipeline
dotnet run scripts/RunDogfood.cs

# 4. Audit packages for known security vulnerabilities
dotnet list package --vulnerable --include-transitive

# 5. Pack pre-release CLI tool
dotnet pack src/Cli/Diffra.Cli.csproj -c Release -o artifacts/package
```

---

## 6. Complete Roadmap & GitHub Issue Reference

- **Active Milestones**:
  - [x] **Phase 1.6**: Local CAS and portable bundles ([Issue #7](https://github.com/rtkelly-labs/Diffra/issues/7)) — merged in PR #12.
  - [x] **Phase 1.7**: Review bundle presentation and local server ([Issue #8](https://github.com/rtkelly-labs/Diffra/issues/8)) — merged in PR #12.
  - [x] **Phase 2**: Graph composition host, DAG validation, external command adapter ([Issue #9](https://github.com/rtkelly-labs/Diffra/issues/9)) — implemented in [PR #20](https://github.com/rtkelly-labs/Diffra/pull/20).
  - [ ] **Phase 3**: Baselines, Git merge-base resolver, typed complexity, promotion controls ([Issue #10](https://github.com/rtkelly-labs/Diffra/issues/10)) — **UP NEXT**.
  - [ ] **Phase 5**: Prove protocol with first adopter Parquet.SourceGenerator ([Issue #11](https://github.com/rtkelly-labs/Diffra/issues/11)).
- **Feature Wishlist**:
  - **[Issue #13](https://github.com/rtkelly-labs/Diffra/issues/13)**: Standardized Code Coverage Ingestion & Quality Gating.
  - **[Issue #14](https://github.com/rtkelly-labs/Diffra/issues/14)**: Longitudinal Metrics, Time-Series Projections & Evolution Graphing.
  - **[Issue #15](https://github.com/rtkelly-labs/Diffra/issues/15)**: Performance Benchmarks & Statistical Latency Distributions.
  - **[Issue #16](https://github.com/rtkelly-labs/Diffra/issues/16)**: Binary Footprint, Package Bundle Size & Dependency Tree Budgeting.
  - **[Issue #17](https://github.com/rtkelly-labs/Diffra/issues/17)**: Visual Regression & Perceptual Image Diffing.
  - **[Issue #18](https://github.com/rtkelly-labs/Diffra/issues/18)**: Wide Structured Events (OTel Emission) & Intertwined Main/PR Object Storage.
  - **[Issue #19](https://github.com/rtkelly-labs/Diffra/issues/19)**: CNCF CloudEvents Specification & Event-Driven Webhooks.
