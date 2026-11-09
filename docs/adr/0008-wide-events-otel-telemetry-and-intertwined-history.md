# 0008: Wide Structured Events, OpenTelemetry Emission, and Intertwined Branch History

- **Status**: Accepted
- **Date**: 2026-09-27
- **Deciders**: Ryan Kelly, Antigravity
- **Inspiration**: Charity Majors (Honeycomb.io) — "Events over metrics: high-cardinality, wide structured events as the single source of truth."

## Context

Traditional software quality and CI/CD monitoring relies on isolated, pre-aggregated metrics (e.g. `coverage: 84.2%`, `duration_ms: 1250`). This conventional approach has severe limitations:

1. **Loss of High-Cardinality Context**: Once a metric is aggregated to a single number, all context is destroyed: which exact lines were missed, which runner executed the job, what compiler optimization level was set, which branch or pull request triggered it, and what Git commit SHA was evaluated.
2. **Telemetry Fragmentation**: CI runners log unstructured text logs to console, upload zip bundles to ephemeral storage (30–90 day retention), and push lossy gauges to time-series databases. There is no unified schema linking CI events to long-term audit baselines.
3. **Isolated PR Ephemerality**: Candidate PR evidence is treated as disposable; once a PR is closed or merged, its observations evaporate, making it impossible to perform retrospective cross-PR analysis or track systemic regressions across concurrent branches.

## Decision

We adopt Charity Majors' wide-event thesis as an architectural foundation for Diffra: **Metrics do not exist in isolation; they are projections over immutable, high-cardinality, wide structured events.**

Diffra's `Evidence` document is inherently a wide structured event containing full provenance, environment dimensions, and typed payload facts. We establish two complementary consumption and export mechanisms, backed by an intertwined branch history graph:

### 1. Dual Export Mechanisms

#### Mechanism A: OpenTelemetry (OTel) Structured Emission
- Diffra CLI and host provide an OTel-compliant exporter emitting wide structured log records and spans (OTLP over gRPC / HTTP/Protobuf or JSON).
- Every evaluation emits a root span representing the workflow run, with child spans for observation, delta computation, and policy evaluation.
- High-cardinality attributes attached to every event:
  - `git.commit.sha`, `git.branch`, `git.merge_base.sha`, `github.pr.number`, `actor.id`
  - `host.arch`, `os.type`, `dotnet.runtime.version`, `ci.runner.id`
  - Canonical `evidence.id`, `delta.id`, `baseline_reference.id`
  - Full structured payload dimensions (line rates, branch rates, memory allocations, compilation timings).
- **Target Systems**: Live streaming directly into Honeycomb, Datadog, Grafana Loki/Tempo, or ClickHouse for real-time exploratory slicing and alerting.

#### Mechanism B: Long-Term Content-Addressed Object Storage (CAS)
- Immutable, verified storage of complete Evidence, Delta, and Assessment documents in content-addressed object stores (local CAS, AWS S3, Google Cloud Storage, Azure Blob).
- Documents are keyed strictly by their canonical SHA-256 digest (`sha256:<hash>`) with a secondary lineage index partitioned by `(subjectId, branch, commit)`.
- Eliminates ephemeral CI artifact expiration; historical evidence remains permanently verifiable, replayable, and diffable across arbitrary spans of time.

### 2. Intertwined History Graph (`main` Spine + PR Lineage)

Rather than evaluating PRs in a vacuum, Diffra structures historical evidence as an **intertwined directed acyclic graph (DAG)**:

```
[commit A] ───► [commit B] ───► [commit C] ───► [commit D]  (main branch spine)
                   │                                ▲
                   ▼                                │ (merge)
              [PR #12: cand 1] ──► [PR #12: cand 2] ┘
                   │
                   ▼ (divergent PR)
              [PR #14: cand 1]
```

- **The Primary Spine (`main`)**: The append-only sequence of trusted, promoted baselines approved on the primary branch.
- **Candidate Branches (PR Lineage)**: Wide events emitted during PR builds branch off at their exact resolved `merge-base` commit.
- **Intertwined Querying**:
  - Reviewers can evaluate a PR not only against its immediate merge-base, but against the overall velocity of `main`.
  - Concurrent PRs can be analyzed side-by-side to detect overlapping regressions or conflicting quality impacts before merging.
  - When a PR merges, its final candidate event seamlessly links to the primary spine without re-computation.

## Consequences

- Zero data loss: All metrics retain 100% of their operational, environmental, and Git provenance.
- Unified observability: Real-time alerting via OTel and permanent baseline integrity via content-addressed object storage.
- Longitudinal intelligence: Continuous history connecting candidate pull requests directly to long-term release trajectories.
