# Diffra — Agent Implementation Plan

This file is the working handover for a **new, independent repository**. Read `VISION.md` first. Treat the examples as a design target, not a claim about current repositories. Record implementation decisions in the new repo. Do not create GitHub issues or publish packages until the repository owner authorizes those external actions.

## Outcome and scope

Deliver an executable vertical slice: source identity → artifact decoder → typed observation → trusted baseline → typed comparison → policy assessment → deterministic report. Make all stages independently replaceable. Prioritize correctness and a reviewable protocol over breadth. The first adopter is Parquet.SourceGenerator, but all core packages and fixtures must be free of Parquet-specific names or assumptions.

Suggested repository layout:

```text
README.md
VISION.md
PLAN.md
docs/protocol.md
docs/trust-and-identity.md
schemas/evidence-v1.schema.json
schemas/baseline-reference-v1.schema.json
schemas/delta-v1.schema.json
schemas/assessment-v1.schema.json
schemas/manifest-v1.schema.json
src/Protocol/
src/Hosting/
src/Cli/
src/Extensions.Git/
src/Extensions.Structured/
src/Extensions.Complexity/
src/Reporting.Html/
tests/Protocol.Tests/
tests/Integration.Tests/
examples/sample-project/
```

Package and command names are provisional. Avoid freezing public API names before the vertical slice is evaluated.

## Phase 0 — Repository and decisions

1. Create the repository, license, README, contribution guidance, CI build/test workflow and reproducible SDK/tool pinning. Copy these two handover files into the repository.
2. Write short architecture decisions for: canonical JSON and hashing, schema/version compatibility, extension discovery (explicit registration initially), baseline trust, policy status vocabulary, and stable collection identity.
3. Publish a minimal glossary and sequence example. Define threat boundaries for untrusted PR artifacts, external commands and HTML output.

**Done when:** a new implementer can identify each component's responsibility, build the skeleton locally and see a CI test result. No runtime framework dependency on Aspire.

## Phase 1 — Protocol and identity

1. Define versioned envelope and manifest schemas with required `typeId`, `typeVersion`, `schema`, `subject`, `producer`, `environment`, `payload`, `artifactRefs` and SHA-256 digest fields. Record exact canonicalization and digest scope. Use stable path normalization and reject traversal, duplicate logical IDs, duplicate JSON keys where relevant, oversized inputs and digest mismatch.
2. Model artifact set → nested group → artifact plus typed observations. Groups include stable ID, parent, role, required flag, baseline role, retention and gate metadata. Keep physical representation/media type separate from evidence type and comparator.
3. Define `Evidence<T>`, typed decoder/observation/comparator/policy interfaces, versioned type registry and explicit migration interface. Typed comparison requires matching type/version or recorded migration. Unknown evidence is retained as opaque, with no semantic pass.
4. Define immutable Evidence, Baseline reference, Delta and Assessment documents, each with a schema and fixtures. A delta cites exact baseline and candidate identities, comparator/version and typed changes, with no policy verdict. An assessment cites the delta identity and exact rules/version, and can be recalculated without collection or comparison. Define immutable results for comparison, policy findings and provenance. Missing, invalid, incompatible and inconclusive are distinct from unchanged/pass.
5. Specify document examples and CLI input/output contracts in `docs/protocol.md`. Include raw artifact digest versus semantic evidence identity, unknown types, and JSON/XML mapping to a shared evidence type.
6. Build a local content-addressed store; writes verify digests and are atomic. Bundle export/import must reproduce identities after extraction.

**Tests:** identical semantic payloads hash alike under the defined rules; changed meaningful field changes digest; malformed paths/manifests, duplicate IDs and altered bytes fail; version mismatch cannot silently compare; XML/JSON as formats are not hardcoded as evidence types.

## Phase 2 — Graph host and extension boundary

1. Implement explicit code-first registration for subjects, producers, baselines, comparators, policies, stores and presenters. Model declared dependencies and input/output type contracts.
2. Validate cycles, missing inputs, duplicate IDs, incompatible types, comparator ambiguity and impossible baseline roles before executing. Execute a DAG in stable order; record status and provenance for every node. Simple sequential execution is sufficient initially.
3. Add an external command adapter that accepts a declared type/schema and a bounded evidence bundle on stdout or disk. Capture executable/version/arguments and exit status, validate output before import and avoid shell interpolation. Permit untrusted jobs only within CI's existing isolation.
4. Create one sample in-process extension and one external producer fixture to demonstrate cross-ecosystem composition.

**Tests:** invalid graphs fail before side effects; a downstream node reports blocked/inconclusive when an upstream node fails; external fixture and in-process fixture yielding equivalent typed values compare the same.

## Phase 3 — Baselines, trust and comparison

1. Implement exact local baseline selection and Git merge-base resolver. Record baseline role, commit, manifest digest, source store and trust status. Refuse a mutable branch label without resolving and recording its commit.
2. Implement a structured JSON decoder and typed numeric/keyed-collection comparison as the main baseline capability; retain text comparison for explicitly byte/text-oriented evidence. JSON object order/whitespace are insignificant; arrays follow declared ordered or key-based semantics; missing/null/type changes remain distinct. Support explicit field exclusions only in a versioned schema or comparator config.
3. Add a typed complexity model with unit, method identity, method count, total/mean, quantiles and outliers. Record scope, methodology and producer/configuration compatibility before comparison; distinguish unavailable, newly added and removed metrics from zero. Make producer methodology/version and file scope explicit. Build comparator and a policy example that can warn on outlier growth while showing normalized values.
4. Implement candidate and verified states for local promotion. Require a clean second production run at the same head commit and digest before approval. Persist approval record tied to evidence and policy identity; invalidate if relevant identities change. No automatic promotion by a PR job.

**Tests:** extraction of numeric values from JSON preserves units and subject identity; +4 absolute and relative changes are computed correctly; zero baseline makes relative change undefined; mismatched scope or methodology is incompatible; a policy-only rule change reuses the delta; formatting-only JSON change is unchanged; keyed collection reorder is unchanged; ordered sequence reorder changes; type/version mismatch is inconclusive; wrong merge-base, altered baseline, branch race, missing baseline and stale approval cannot pass; policy change recalculates assessment without regenerating evidence.

## Phase 4 — Report and CLI

1. CLI commands: `collect`, `diff`, `eval`, `report`, `baseline verify`, `baseline update --mode missing|changed|all`, `bundle export|import`. `collect` emits evidence, `diff` emits a policy-free delta, `eval` assesses that delta under versioned rules, and `report` accepts a delta with optional assessment. `update` prepares candidates; approval/promotion is a separate explicit operation. Make help and exit codes distinguish no change, policy failure and invalid evidence.
2. Define a generic review model with group summaries, typed differences, findings, provenance, attachments and trust status. Render deterministic JSON and one self-contained HTML file with escaped untrusted content and accessible structure. Reports show candidate and baseline role, commits/digests, comparator and policy versions.
3. Keep report-kit version/digest in presentation identity. First use bundled CSS; later support pinned design-system report kit without a runtime network dependency.

**Tests:** identical inputs produce identical report bytes after intentional volatile fields are excluded or normalized; malicious HTML in evidence is escaped; a style-only renderer change changes presentation identity but leaves evidence approval valid; missing evidence is visible, never green.

## Phase 5 — Prove with an adopter

1. Integrate a small Parquet.SourceGenerator slice: generated-source text, a typed public API or complexity sample, and a manifest. Adapt existing derived artifacts rather than moving the protocol into that repository.
2. Compare a real branch against its trusted merge-base and inspect the report with maintainers. Confirm that refreshed snapshots cannot bypass compilation/API semantic checks in the adopter's existing pipeline.
3. Export one release-style evidence bundle and verify it can be imported and compared offline. Exercise the external producer fixture with a Node or Python script.
4. Gather API friction and revise extension interfaces before publishing a stable package. Document explicit migration from existing golden checks, including what remains gated by native project tests.

**Done when:** a reviewer can answer what changed, whether it is acceptable, which baseline and tools were used, and which artifacts support the claim without reading raw diffs alone.

## Backlog after validation

- XML/CSV adapters mapping to shared evidence types; archive/NuGet layout, .NET API, graph and image comparators. Image integration should produce multiple named observations (pixel mask, changed area, regions and structural measure) and multiple review views (overlay, blink, slider, heatmap) without conflating views with policy. Declare alignment, masks, colour handling and capture environment.
- Approved-main and previous-release resolvers; trusted CI artifact and release stores; release-to-release reports and retention.
- Pinned design-system static report kit and visual regression for its reference fixture.
- Multi-run benchmark distributions, environment comparability and uncertainty; trends over more than two baselines; coverage, architecture and security evidence families.
- Remote extension catalogue, caching/parallel scheduling and dashboards only after the contract stabilizes.

## Implementation rules and review checkpoints

- Every new evidence family supplies a schema, documented identity/order semantics, comparable scope/methodology, appropriate comparison mechanisms and meaningful fixtures. Do not default structured formats to text diff.
- Preserve raw artifact digest alongside semantic identity. Do not claim semantic equivalence for an unknown type.
- A measurement states unit, scope and method; comparisons and policies reference exact inputs and versions. Never encode a policy verdict into a producer's payload.
- Baseline lookup is explicit, immutable once resolved and trust-aware. Candidate production and approval are separate, with head/digest checks and a clean verification pass.
- Keep optional integrations outside the core. Use a second ecosystem fixture to prevent accidental .NET-only wire assumptions.
- At each phase, update protocol examples and run end-to-end import/compare tests. Prefer a small demonstrated path to speculative general interfaces.

## Suggested initial issue sequence

1. Repository skeleton, CI and architecture decisions.
2. Evidence envelope, schemas, canonical identity and adversarial validation.
3. Artifact grouping, typed registry and migration rules.
4. Local immutable store and portable bundle.
5. Graph composition, validation and external command adapter.
6. Git merge-base resolver and trust model.
7. Structured decoder, keyed collection and typed numeric comparison mechanisms.
8. Semantic JSON and explicit text comparators.
9. Typed complexity evidence and policy example.
10. Candidate verification, approval record and race checks.
11. Review model, deterministic JSON/HTML and CLI.
12. Parquet vertical slice and offline release bundle proof.

Each issue should name a testable acceptance criterion from its phase; split further only if implementation reveals a concrete dependency. Keep this ordering flexible where parallel work is independent.

## Diffra CLI contract checkpoint

The first end-to-end fixture should run these independent steps (exact flags may change before public release):

```sh
diffra collect -o evidence.diffra.json
diffra diff --baseline main.diffra.json --evidence evidence.diffra.json -o delta.diffra.json
diffra eval --rules policy.yaml --delta delta.diffra.json -o assessment.diffra.json
diffra report delta.diffra.json --assessment assessment.diffra.json --format markdown
```

`policy.yaml` is illustrative; implement JSON rules first if that keeps the initial release smaller. Verify that changing rules changes only the assessment, while a renderer upgrade changes only presentation identity. A report from a delta alone describes measured changes and explicitly has no policy verdict.

## Comparison design checkpoint and adversarial fixtures

Before publishing extension APIs, document the registry contract for decoders, typed observations, comparators and policies. Selection must consider type/version, scope, methodology and declared capabilities. Comparator output is a versioned delta containing named measurements and provenance; a policy cannot mutate it. Review views are derived from immutable inputs and results.

Use these fixtures to settle semantics and report wording:

1. Differently formatted JSON or XML decodes to equivalent typed evidence; order matters only when the type declares it.
2. Structured metrics extract per-subject numbers. Check absolute/relative deltas, zero baselines, units, bounds, direction, keyed identity, new/removed/missing values, and outliers.
3. Equal numeric values from incompatible methodology or scope yield `incompatible`, never `unchanged`.
4. A noisy benchmark has repeated samples; its distribution comparison reports uncertainty and cannot be treated as a certain regression from one number.
5. An image pair yields several observations and review views. Check explicit masks, alignment, colour handling, dimensions and comparator version. A changed overlay presentation leaves evidence identity intact.
6. A required group missing from the baseline, a stale baseline, and an unsupported schema migration cannot silently pass.
7. Changing policy thresholds recalculates only Assessment from the same Delta; changing report assets changes only presentation identity.

The first release must implement fixtures 1–3 and 7. Fixtures 4–6 specify extension boundaries and can initially be exercised with a minimal stub and an explicit `unsupported` outcome; implement the full mechanisms after the vertical slice.
