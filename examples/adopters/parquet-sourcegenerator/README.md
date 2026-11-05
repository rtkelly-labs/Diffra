# Diffra Adopter Integration: Parquet.SourceGenerator

This directory demonstrates Phase 5 of Diffra's architecture: proving the protocol with a real-world adopter (**Parquet.SourceGenerator**).

## 1. Division of Responsibility & Gating Integrity

A central invariant of Diffra is that **semantic baselines never replace native project compilation or test execution gates**. Instead, Diffra provides structured differential evidence and presentation over derived outputs.

| Responsibility | Mechanism | Owner |
|---|---|---|
| **Compilation Verification** | Roslyn compilation of generated code against target model declarations (`ERRORS == 0`). | Native test suite (`GoldenCodeGenRegressionTests.cs`) |
| **Package API Surface** | Roslyn symbol analyzer gating `PublicAPI.Unshipped.txt` (RS0016). | Roslyn Analyzer |
| **Internal Seams** | Seams analyzer gating `src/api/seams.txt` (PARQAPI002). | Roslyn Analyzer |
| **Semantic API Evolution** | Keyed collection comparison of exported model API catalogues (`*.api.txt`), detecting breaking removals and modifications while ignoring innocent reorderings. | **Diffra (`diffra.api-surface.v1`)** |
| **Emitted Source Code Tracking** | Normalized source text and digest comparison of emitted generator outputs (`*.g.cs`). | **Diffra (`diffra.source-text.v1`)** |
| **Cyclomatic Complexity Tracking** | Per-method complexity distribution, quantiles (p50..p99), and outlier detection. | **Diffra (`diffra.complexity.v1`)** |
| **Merge-Base Trust & Promotion** | Content-addressed baseline references resolved to exact Git merge-base commits with two-run verification. | **Diffra (`diffra baseline`)** |
| **Review Presentation** | Standalone HTML review bundles and deterministic JSON deltas. | **Diffra (`diffra present`)** |

## 2. Ingested Derived Artifacts

Parquet.SourceGenerator generates derived review artifacts under `artifacts/` via `scripts/DerivedOutputs.cs`. Diffra's adopter adapter (`Adapter.cs`) translates these into canonical Diffra evidence documents:

1. **`artifacts/golden/{Model}ParquetExtensions.api.txt`** $\rightarrow$ `diffra.api-surface.v1`
   - Encodes exported public API members as keyed collections.
   - **Keyed Collection Invariance**: Reordering members in `.api.txt` produces zero diffs (`unchanged`), eliminating git noise while preserving exact symbol identity.
   - New methods are classified as `api/member-added` (non-breaking pass).
   - Removed or modified signatures are classified as breaking changes (`fail`).

2. **`artifacts/golden/{Model}ParquetExtensions.g.cs`** $\rightarrow$ `diffra.source-text.v1`
   - Tracks line count, character count, and SHA-256 source digests.

3. **Generated Code Complexity** $\rightarrow$ `diffra.complexity.v1`
   - Quantiles and outlier tracking preventing complexity regressions in emitted emitter methods.

4. **Manifest Document** $\rightarrow$ `manifest-v1.schema.json`
   - Binds the artifact set with roles, SHA-256 digests, and retention policies.

## 3. Offline Bundle Workflow

Diffra packages all artifacts and evidence into an air-gapped evidence bundle:

```bash
# Export evidence bundle for a PR or release
diffra bundle export --manifest manifest.json --out parquet-golden-evidence.tar.gz

# Inspect and verify offline in an air-gapped environment
diffra bundle import --bundle parquet-golden-evidence.tar.gz --store /tmp/cas-store/
diffra present bundle --bundle parquet-golden-evidence.tar.gz --serve --port 8080
```
