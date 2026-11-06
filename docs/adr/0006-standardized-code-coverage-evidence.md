# 0006: Standardized Code Coverage Evidence & Regression Gating

- **Status**: Accepted
- **Date**: 2026-09-27
- **Deciders**: Ryan Kelly, Antigravity

## Context

Code coverage is one of the most widely used quality indicators in software engineering, yet existing tooling suffers from severe determinism and signal-to-noise deficiencies:

1. **Non-Deterministic Artifacts**: Coverage outputs (Cobertura, lcov, JaCoCo, Coverlet) include absolute filesystem paths (`/Users/.../src/File.cs`), run timestamps, machine hostnames, and arbitrary element ordering. These non-canonical representations prevent content-addressed hashing and reliable diffing.
2. **Coarse Percentage Flaws**: Traditional coverage gates rely on a global aggregate percentage (e.g. "80% threshold"). A pull request can delete 500 lines of well-tested code while adding 200 lines of completely untested critical business logic and still register a net percentage *increase*.
3. **Flaky Global Swings**: Normal refactoring or unrelated test runs can introduce minor fluctuations (+/- 0.02%), causing spurious CI failures that erode developer trust in quality gates.

Diffra requires a standardized, deterministic evidence type for code coverage that enables high-signal regression gating.

## Decision

We establish a standardized canonical coverage evidence model (`diffra.coverage.v1`) and a typed coverage delta comparator (`diffra.coverage-comparator.v1`):

1. **Deterministic Normalization**:
   - Ingestion strips runtime noise, timestamps, and machine metadata.
   - All source paths are converted to canonical, forward-slash, repository-relative paths (`src/Protocol/CanonicalJson.cs`).
   - Modules, files, and classes are sorted using UTF-16 ordinal ordering.
   - Uncovered lines and branches are encoded as canonical sorted interval strings (e.g. `"14-18, 25, 40-42"`).

2. **Hierarchical Coverage Payload**:
   - Aggregate line, branch, and method metrics (total, covered, missed, ratio).
   - Per-file breakdown tracking line and branch coverage per relative path.

3. **High-Signal Delta Evaluation**:
   - Compares candidate coverage directly against the trusted `merge-base` baseline.
   - Detects **Newly Uncovered Lines**: Evaluates Git diff hunk boundaries against candidate uncovered line intervals to identify newly added or modified lines that lack test coverage.
   - Supports targeted policies:
     - `minOverallLineCoverage`: Absolute floor (e.g. 80.0%).
     - `maxOverallCoverageDrop`: Permissible drop budget across PR (e.g. 0.0% for strict no-drop).
     - `failOnNewlyUncoveredLines`: Strict gating rejecting any new lines without tests.

4. **Self-Verification Principle**:
   - Diffra will ingest and gate its own test coverage (`Protocol.Tests`) using this model as part of its build and CI lifecycle.

## Consequences

- Coverage becomes a verifiable, content-addressed Evidence document (`sha256:...`).
- PR reviews highlight exact line-level regressions rather than confusing aggregate percentage shifts.
- Adopters can integrate coverage from any engine (Coverlet, llvm-cov, JaCoCo, c8) by mapping into `diffra.coverage.v1`.
