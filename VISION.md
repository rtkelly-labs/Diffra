# Diffra — Vision

Status: design handover, 26 September 2026. This is a proposed independent repository, not a description of implemented software.

## Purpose

Build Diffra, a small, extensible typed comparison and baseline protocol for evidence-based improvement of software projects. A project composes evidence producers, typed evidence, baseline resolvers, comparators, policies and reports. The system decodes heterogeneous artifacts into typed observations, applies appropriate comparison mechanisms, and explains decisions without conflating evidence with policy.

**Core rule:** Artifacts preserve evidence. Types give evidence meaning. Comparators describe change. Policies decide acceptability. Reports communicate the result. Approval binds to evidence identity.

Parquet.SourceGenerator is the first proving ground. The design system and Arrow.SourceGenerator are later consumers. The protocol lives in its own repository and does not import their internals. Aspire is an analogy for code-first composition and integrations, not a runtime dependency.

## Vocabulary and boundaries

| Concept | Responsibility |
| --- | --- |
| Subject | Repository, commit, package, document, site, or other thing measured |
| Producer | Creates artifacts and typed evidence from an identified subject and environment |
| Artifact | Stored bytes, with digest, media type and provenance; text/binary is representation, not meaning |
| Evidence type | Stable namespaced ID and version, schema and semantic rules |
| Evidence envelope | Type, subject, producer, environment, provenance, payload digest and optional artifact references |
| Artifact set/group | Review and retention organization; nested groups may inherit policy, with explicit overrides |
| Baseline resolver | Selects a trusted reference by role and records exact identity |
| Comparator | Compares compatible evidence and emits typed differences or an explicit incompatibility |
| Policy | Assesses differences against versioned rules, independently of production/comparison |
| Assessment | Findings and their supporting evidence, with policy identity |
| Store | Persists immutable content and locates evidence/baselines |
| Presenter | Renders a generic review model; cannot silently change evidence or approval |

A file may support several observations. A NuGet archive can yield entry layout, dependencies, public API and byte digest. A screenshot can yield a pixel comparison and accessibility observations. A JSON or XML document should normally be validated and mapped to a schema-aware type; whitespace and property/attribute order are not semantic changes. Collection identity and whether order matters belong to the evidence type. Byte comparison remains available when exact serialization is the intended contract.

## Composition model

The host composes a dependency pipeline. Extensions register capabilities, declared input/output type IDs, schemas, canonicalizers, producers, comparators, policies, baseline resolvers, stores or presenters. Each node has stable identity and explicit dependencies. The host validates missing capabilities, duplicate registrations, incompatible versions, cycles and ambiguous comparator choice before execution. Execution records statuses including produced, unchanged, changed, unavailable, invalid and inconclusive; unavailable must never become a pass.

The first host can be a .NET CLI with an in-process typed API. The portable boundary is an envelope and schema that a Node, Python, Rust or external command producer can emit. In-process `Evidence<T>` and `Comparator<T>` preserve compile-time types; the wire format uses explicit type IDs and versions. No `object` bag is used for semantic comparison. Unknown types can be stored and displayed as opaque attachments but cannot receive a semantic PASS.

Illustrative composition (design intent, not committed API):

```csharp
var source = builder.AddGitSubject("source");
var complexity = builder.AddExternalProducer<ComplexityReport>("complexity", source);
var baseline = builder.AddMergeBaseBaseline("pr", source);
var comparison = builder.Compare("review", complexity, baseline);
builder.AddPolicy("complexity-budget", comparison);
builder.AddHtmlReport("report", comparison);
```

## Identity, trust and reproducibility

An immutable evidence identity covers canonical typed payload, evidence type/schema, subject commit, producer/version, relevant tool/environment identity and referenced artifact digests. A comparison identity additionally includes baseline role and exact baseline evidence identity plus comparator/version. A policy assessment identifies policy/version/configuration. Presentation identity covers renderer/schema/report-kit digest separately. Define canonical byte rules, hash framing and field inclusion in a versioned specification; avoid hashing a self-referential manifest.

Baseline roles have distinct questions: **merge-base** asks what the PR changed; **approved-main** asks whether it differs from accepted state; **previous-release** asks what users receive differently. The resolver returns an exact commit and trusted manifest digest, never a mutable `latest` pointer as identity. A PR can propose candidate evidence, but cannot promote itself to a trusted baseline. Promotion requires an explicit review decision tied to candidate digest and head commit, then a clean second verification against that same identity. Missing, corrupt or incompatible baselines fail or become explicit inconclusive candidates; none silently self-heal.

Storage and approval are independent. Stores can later include Git, CI artifacts, release assets and cloud storage. The first implementation uses a local content-addressed store and a Git-aware resolver. Retention and promotion policies belong to groups and deployment configuration, not to a producer. Keep user-generated or PR-supplied artifacts untrusted until verified; guard paths, sizes, digests and parsers at import.

## Comparison and assessment

Comparison is type-specific. A generic structured-tree comparator can handle JSON/XML with canonicalization, paths, keys, unordered collections and schema validation. Domain comparators then add meaning: complexity distribution, API compatibility, archive contents, graph topology, image pixels, etc. A stored format change can be semantically unchanged if both inputs decode to the same versioned type. Migration across type versions must be explicit and recorded; unsupported migration is inconclusive.

Measurements carry units, direction, aggregation, sample scope and methodology. Cyclomatic complexity is an example: total, method count, mean, quantiles, outliers and stable method identities may be compared together. A policy can distinguish total growth due to added methods from growth in high-risk outliers. Comparators report deltas; policy assigns pass, warn, fail or needs-review; a human can inspect the underlying evidence. Composite assessments cite inputs and preserve disagreements rather than collapsing everything into one score.

Groups organize contracts, architecture, quality, distributables and presentation. A group can define required evidence, baseline role, gate, approval and retention. Individual artifacts may have more than one observation or comparison. The report summarizes groups before details and offers attachments without forcing raw binary diffs.

## Comparison mechanisms are the foundation

A decoder turns artifact bytes (JSON, XML, CSV, images, archives or binaries) into typed observations. An evidence type specifies subject identity, units, collection keys/order, methodology, scope and comparability rules. A comparison mechanism consumes compatible observations and emits a factual, typed delta. A policy interprets that delta. A presenter may offer several views of the same inputs without changing the delta or verdict. File format alone never chooses the comparator.

Mechanisms are independently registered capabilities, for example exact bytes, normalized text, structural tree, keyed collections, numeric value, numeric distribution, trend, graph topology, archive layout and image comparison. A domain comparator may compose mechanisms (for example method-level complexity plus distribution and outlier changes) and return several named observations. Composition must declare inputs, parameters, implementation version and output types; the host rejects incompatible or ambiguous selections. No universal `changed` or percentage threshold is meaningful across every type.

**Numerical evidence:** A measurement carries metric ID, stable subject ID, value, unit, direction if defined, scope, methodology and producer/configuration identity. A numeric delta records previous/current values, absolute and relative changes where valid, and missing/new status; division by zero is explicit. Policies can use absolute budgets, relative budgets, upper/lower bounds and outlier counts. Method-level complexity may be matched by stable symbol identity, while aggregates compare totals, counts, means and quantiles. Methodology or scope mismatch is incompatible unless a recorded normalization/migration explicitly permits comparison. Benchmarks with repeated samples require a separate distribution comparator that reports sample sizes and uncertainty; a single-point numeric rule must not imply statistical confidence.

**Images:** Keep baseline and candidate images immutable. A comparator can emit pixel difference masks, changed-area measures, regional measurements and structural similarity; each result names the algorithm/version, image decoding, dimensions, colour handling, alignment, masks, regions and thresholds. Side-by-side, opacity overlay, blink, wipe slider and heatmap are complementary review views, not interchangeable policy decisions. Reproducible capture environment and optional semantic companions (for example token colours, layout dimensions and accessibility checks) remain separate typed evidence. Masking or alignment must be explicit so a transform cannot silently hide a regression.

The same structured artifact may yield multiple observations; the same observation may be assessed by multiple independent policies. Reports retain the chain from artifact digest through decoder, observation, comparator and delta to each finding. No result becomes a PASS solely because an unrecognized adapter produced valid JSON.

## Reports and releases

The report takes a manifest and comparison/assessment results, independent of producers. It emits deterministic, self-contained HTML and machine-readable JSON. Report-kit assets may later come from a pinned design-system release with verified digest, embedded in the HTML. Changing presentation must not invalidate approval of unchanged evidence. HTML screenshot regression can test the report kit independently.

Release evidence should preserve the exact shipped artifacts, manifest, prior-release comparison, policy results and report under immutable release identity. The first release of this protocol need only export a portable evidence bundle; integration with release publishing follows validation in Parquet.

## Deliberate first-version limits

Start with .NET host/CLI, local store, Git commit and merge-base identity, JSON envelope, text and schema-aware JSON comparisons, one typed complexity example, policy assessment and self-contained report. Include a command adapter to prove an external ecosystem can supply evidence. Do not initially build a distributed scheduler, plugin registry, cloud store, approval web service, screenshot comparator or broad catalogue of metrics. Keep extension interfaces narrow and versioned before third-party publication.

## Success test

A clean checkout can produce the same evidence identity for the same inputs; compare a PR candidate with an exact merge-base baseline; explain a JSON reorder as unchanged, extract numerical metrics from structured data, distinguish measured changes from policy regressions, and present multiple image review views from one comparison; report missing or untrusted baselines clearly; and render the same evidence through a second presenter without altering approvals. A second non-.NET producer can participate through the portable envelope without referencing the .NET host.

## Diffra names and portable documents

**Tagline:** Typed evidence. Trusted baselines. Reviewable change. The specification is the Diffra Protocol; the executable is `diffra`. Configuration and manifest names may use `.diffra.json` or `.diffra.yaml`, but the initial interchange format is versioned JSON. Names and examples do not imply that a YAML parser is required for the first version.

The protocol defines four independently versioned documents: **Evidence** (typed observations and artifact provenance), **Baseline reference** (trusted selection resolving to an immutable evidence identity), **Delta** (typed changes without policy verdict), and **Assessment** (findings under an exact policy version). A report consumes the delta and, when policy outcomes are shown, the assessment. A renderer cannot infer pass/fail from raw change. Keep source artifact bytes and digests alongside semantic values.

Adapters and integrations may be written in different ecosystems. They advertise produced type IDs and schema versions, validate their inputs, and emit a bounded portable evidence envelope. Comparison is selected by evidence type and version, not file suffix. JSON/XML adapters can map to the same typed value; collection identity, order, number semantics, missing/null behavior and exclusions are declared by the type contract. The host checks capability compatibility before execution.
