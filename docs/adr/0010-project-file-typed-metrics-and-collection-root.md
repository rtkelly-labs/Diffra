# ADR 0010: Project file, typed metrics and the collection root

- Status: proposed
- Date: 2026-11-06

## Context

The first adopter integration (Parquet.SourceGenerator PR #752, shadow mode) emits Diffra metrics
payloads from BenchmarkDotNet results. Every workaround it needed is a gap in the current contract:

- **One flat rules set per eval.** Allocations, GC counts and time need different budgets, so the
  integration split into three payloads with three rules files.
- **Thresholds are OR'd, with no absolute floor.** The adopter's existing gate fails at "> 4 KB *and*
  > 5%". Relative thresholds misfire on 0–3 B cases, so the allocation budget had to be absolute only.
- **No types.** Every value is a decimal string. GC counts were hand-normalised per 1000 operations.
- **Budgets only describe increases.** Coverage regresses when it *decreases*; `SeverityFor` treats every
  decrease as a pass.
- **Methodology does two jobs.** It blocks comparison and it partitions history. On the same code,
  allocations held across AMD and Intel runners (≤ 152 B), but gen0 GC counts changed 2–5× because the
  runtime sizes gen0 from the CPU cache, and time changed 1.7–2.1× across CPU models. A single
  concatenated methodology string cannot say "allocations compare across CPUs, GC counts do not".
- **Nothing says what a complete run is.** The integration refuses to write an empty payload, because an
  empty payload reads as "everything removed". That rule lives in one adopter script.
- **Every budget came from variance**, worked out by hand from six CI artifacts as "about k × the worst
  same-commit noise". History should be able to suggest these budgets.

This ADR adds one small, schema-validated policy file per adopter, `diffra.project.yml`, and one root
document per collected commit, `diffra.collection.v1`. It designs the seams for series evaluation; it
does not build the series engine, change-point detection, budget suggestion or a full emitter SDK.

## Decision

### 1. The project file

`diffra.project.yml` is YAML validated against
[`schemas/project-v1.schema.json`](../../schemas/project-v1.schema.json). It declares:

| Section | Purpose |
| --- | --- |
| `schema` | Constant schema URI, as in every other Diffra document. |
| `scope` | The stable scope the project's evidence belongs to. |
| `dimensions` | Planned and observed environment dimensions (section 6). |
| `types` | Optional custom composite types: the escape hatch (section 3). |
| `metrics` | Named metrics. Each uses a standard type, a leaf type or a custom type. |
| `limits` | Fail-closed size limits (section 9). |

Metric names are camelCase identifiers (`^[a-z][A-Za-z0-9]*$`), so they never need JSON Pointer
escaping. The schema is strict: `additionalProperties: false` everywhere, and each type admits only the
rules that make sense for it. The schema checks rules wherever the type is visible from the metric
itself (a leaf type, or a field of a standard type). Rules on fields of a *custom* composite are checked
by the loader against the declared field type; the schema checks only their shape.

YAML is an authoring format only. The loader parses it to the JSON data model, rejects duplicate keys,
anchors, aliases, tags and non-string map keys, and validates the result. Policy identity is the SHA-256
of the exact source bytes, as for every policy document (see [trust and identity](../trust-and-identity.md)).

### 2. Standard types are the default

Most projects should not declare types at all. Diffra ships a versioned **standard library**. A standard
type bundles:

- its **fields**, each a named, typed metric with its own collection semantics and keying;
- **default stratification** per field;
- **default rules**, including which direction is worse;
- the **converters** that produce it, each with a fixed input format and key derivation;
- what a **complete collection** of it is (section 8).

A project names the type and its input, and overrides only what differs:

```yaml
metrics:
  benchmarks:
    type: diffra/benchmark@1
    from: { benchmarkdotnet: "BenchmarkDotNet.Artifacts/results/*-report-full.json" }
    fields:
      allocatedBytes:
        versus: { fail: { increase: { above: 1024B } } }
```

**Overrides replace, field by field and mode by mode.** Declaring `versus` on `allocatedBytes` replaces
that field's default `versus` rules entirely; its default `check` and `series` rules stay. There is no
merge inside a mode, so a reader sees the whole effective `versus` policy in one place. An override is
validated against the rules the field's type allows, exactly as a custom metric would be.

An earlier sketch proposed `use: benchmarks.allocatedBytes` to alias one field as a new metric.
This ADR drops aliasing: it gives one measurement two names, two series and two findings. Field
overrides under `fields:` give the same control without a second identity.

#### Minimum viable standard library

| Type | Fields | Converters (`from:` keys) | Keys | Default rules |
| --- | --- | --- | --- | --- |
| `diffra/test-results@1` | `results`: `map<test-id, testResult>` (variant, below) | `junit`, `trx` | Fully qualified test name plus a stable display-name suffix for data rows | `check`: `fail` when a result's tag is `error`. `versus`: `needs-review` on `success → skipped`; `needs-review` on removed keys. |
| `diffra/coverage@1` | `lineRate`, `branchRate`: `ratio`; `coveredLines`, `totalLines`: `count` (lines); `fileLineRate`, `fileBranchRate`: `map<path, ratio>` | `cobertura`, `opencover` | Repository-relative, forward-slash, normalised path (ADR 0006) | `versus`: `warn` on `lineRate` decrease above `0.005`. Per-file maps report only. |
| `diffra/benchmark@1` | `allocatedBytes`: `map<case, bytes>`; `gen0PerKOps`, `gen1PerKOps`, `gen2PerKOps`: `map<case, decimal>` (`count/1000-ops`); `meanTime`: `map<case, duration>` | `benchmarkdotnet` | `Type.Method(Parameters)` exactly as BenchmarkDotNet prints it | `allocatedBytes`: stratify `[os, arch, runtime]`, `warn` on increase above 5% and above 256B. GC and time: stratify `[os, arch, runtime, cpu]`, `warn` only. |
| `diffra/findings@1` | `total`: `count` (findings); `byRule`: `map<rule-id, count>` (findings) | `sarif` | `toolName/ruleId` | `versus`: `warn` on any `total` increase; `needs-review` on added rule keys. |
| `diffra/size@1` | `total`: `bytes`; `files`: `map<path, bytes>` | `files` (a glob) | Repository-relative normalised path | `versus`: `warn` on `total` increase above 5% and above 4KiB. |

Unless a field says otherwise, standard fields stratify by `[os, arch, runtime]`, so a Windows run is
never judged against a Linux baseline. `diffra/benchmark@1` adds `cpu` for its GC and time fields. Rules
under `when` (variant fields) inherit their map's stratification; they cannot stratify on their own.

The principle behind every default: **a default never fails a build on a noisy measurement.** Defaults
fail only on deterministic regressions (a test that now errors). Budgets on timing, GC and coverage start
as `warn` and a project tightens them deliberately.

Left out of the first library: complexity (already a typed evidence document with its own comparator),
API surface (structural, better served by the keyed collection comparator), mutation testing and
binary-size breakdowns by symbol. Each can join later as a new standard type without changing this ADR.

#### Versioning and upgrade

The `@N` is part of the methodology (section 6). Any change to a standard type's fields, encodings, key
derivation or completeness rule is a new major version, `@N+1`. Default rules are *not* part of the
methodology: they are policy and may change in a Diffra release, because policy changes produce a new
Assessment from the same Delta (ADR 0004). A release note lists every default-rule change.

Upgrading `@1` to `@2` in a project file is a policy change made in a pull request. The first collection
under `@2` has no `@2` baseline, so its versus rules are `inconclusive` until a trusted `@2` record exists
at a merge base. A project that cannot tolerate that gap runs both versions side by side for one
promotion cycle, as two metrics, and removes the old one afterwards.

Converter versions follow the same rule. A converter's version changes only when its output can change
for the same input; a converter version change is a methodology change and therefore `incompatible`, not
a measured change.

### 3. Type system

#### Leaf types

Each leaf type has exactly one canonical encoding (section 11) and admits a fixed set of rules.

| Type | Meaning | Unit | Rules |
| --- | --- | --- | --- |
| `count` | Non-negative integer | Declared by the project | `check`: `above`/`below`. `versus`/`series`: `increase`/`decrease`, absolute or relative, with `and` floor. |
| `integer` | Signed integer | Declared | As `count` |
| `decimal` | Signed exact decimal | Declared | As `count` |
| `bytes` | Non-negative byte count | Fixed `B` | As `count`; absolute thresholds take `B`, `KiB`, `MiB`, `GiB` |
| `duration` | Non-negative elapsed time | Fixed `ns` | As `count`; absolute thresholds take `ns`, `us`, `ms`, `s`, `m`, `h`, `d` |
| `ratio` | Bounded fraction, 0 to 1 inclusive | Fixed `ratio` | As `count`, but **absolute thresholds only** |
| `datetime` | UTC instant | None | `check`: `age`. `versus`: `increase`/`decrease` by a duration |
| `enum` | One of a declared member list | None | `check`: `is`. `versus`: `changed`, optionally `except` listed transitions |
| `digest` | A SHA-256 content digest | None | `versus`: `changed` |
| `text` | Converter-normalised string | None | `versus`: `changed` |

Why these, and not others:

- **`ratio` earns a place** because it changes which rules make sense. A relative change of a ratio (a
  50% drop from 0.02 to 0.01) misleads, so ratios accept absolute thresholds only. `percentage` does not
  earn a place: it is a ratio × 100, and two encodings of one quantity break canonical identity.
- **`count` earns a place** over `integer` because non-negativity is checkable at emit time and is what
  every counter is.
- **`duration` and `bytes`** fix their unit, so "wrong unit" becomes a type error at emit time rather
  than a silent 1000× change.
- **`digest`** is equality-only and is how a large artifact participates as a metric without its bytes.
- **`text`** exists for test messages. It admits only `changed`. Normalisation (stripping absolute paths,
  line numbers, addresses and timestamps) is the converter's job and part of its methodology. Diffra
  never normalises text itself and does not normalise Unicode (ADR 0001).
- **`version` (SemVer) is left out.** Its ordering rules need full SemVer 2.0 precedence, and today's
  uses (tool and dependency versions) belong in the environment or methodology, not in a metric.
- **`boolean` is left out.** It is an `enum` with values `[false, true]`; a second type would add a second
  encoding of the same fact.

The `age` rule on `datetime` is a `check` rule, measured against the candidate subject commit's committer
timestamp, never against wall-clock time, so the same inputs always produce the same Assessment.

#### The composite type: `variant`

A **variant** is a tag and, for each tag, a fixed set of fields. Fields of one tag never appear with
another tag. A variant is unordered: `increase` and `decrease` on the tag are invalid, and the schema
rejects them. Variant fields are leaf types; a variant cannot nest another variant or a collection in v1.

There is no general `record` type. A standard type's fields are separate typed metrics that share a
converter, which is what multi-valued measurements such as a benchmark case need. A `record` can be
added later if a custom source needs one value with several fields.

#### Collections and ADR 0005

ADR 0005 says arrays are ordered unless a type declares them keyed. The project file makes that
declaration explicit and adds two unordered forms:

| `collection.kind` | Encoding | Identity | Rules |
| --- | --- | --- | --- |
| *(absent)* | A single value | — | The leaf or composite rules |
| `map` | JSON object keyed by the canonical key | ADR 0005 keyed collection; the key is the map key | Leaf or composite rules per key, plus `keys.added` / `keys.removed` severities |
| `ordered` | JSON array in emitted order | ADR 0005 default | `changed` only |
| `set` | JSON array, unique, sorted by UTF-16 ordinal of each encoded member | Members are their own keys | `added` / `removed` severities only |
| `distribution` | JSON array of a numeric type, sorted ascending | A multiset of samples | Numeric rules apply to the declared `summary` statistic |

A `map` is the keyed collection: keys are present, non-null and unique by construction (JSON object
names), and `keys.normalization` declares how they are derived (`none` or `repository-path`). A changed
key is one removal and one addition, as ADR 0005 requires. Keys are addressed as JSON Pointer tokens,
escaping `~` as `~0` and `/` as `~1`, so `Type.Method(a/b)` is `/allocatedBytes/Type.Method(a~1b)`.

Defaults for maps: `keys.added` is `pass` (recorded, not judged) and `keys.removed` is `needs-review`. A
removed key is never silently a pass.

#### Custom types: the escape hatch

```yaml
types:
  fixtureCheck:
    variant:
      accepted: {}
      rejected: { message: text }
metrics:
  schemaFixtures:
    type: fixtureCheck
    collection: { kind: map, keys: { normalization: repository-path, removed: fail } }
    from: { payload: "artifacts/diffra/fixtures.json" }
    versus: { fail: { transition: [{ from: accepted, to: rejected }] } }
```

Custom types use the same primitives as the standard library. Custom metrics read a Diffra metrics
payload through the `payload` converter, so the tool that wrote the payload is the emitter.

### 4. Rules

Every metric or field may carry three rule modes:

| Mode | Inputs | Question |
| --- | --- | --- |
| `check` | The candidate alone | Is this value acceptable on its own? (coverage below a floor, a test in `error`) |
| `versus` | Candidate and its baseline (section 7) | Did this change regress? |
| `series` | Candidate and a window of history (section 7) | Is this a shift against recent history? |

Each mode maps severities to rule sets: `warn`, `needs-review` and `fail`, from ADR 0004. Within one
rule set, the keys are OR'd: a value triggers the severity when any listed rule triggers. When several
severities trigger, the most severe wins, in ADR 0004 order (`fail` > `inconclusive` > `needs-review` >
`warn` > `pass`).

#### Numeric change rules and the `and` floor

```yaml
versus:
  fail: { increase: { above: 50%, and: { above: 30 } } }
  warn: { decrease: { above: 0.005 } }
```

- `increase` and `decrease` each take a threshold `above` and an optional `and`, which is a second
  threshold the change must *also* exceed. Thresholds are absolute (in the type's unit) or relative (a
  `%` string).
- **Zero baseline.** A relative threshold against a zero baseline is undefined, as today. The `and` is
  evaluated as a conjunction with three-valued logic: if any leg is known to be false, the rule does not
  trigger; if every known leg is true and a relative leg is undefined, the rule yields `needs-review`
  instead of its severity. A gen2 count that rises from 0 to 12 under `above: 50%, and: { above: 30 }`
  therefore passes (the absolute leg is false); one that rises from 0 to 40 needs review.
- A rule with only a relative threshold behaves as `SeverityFor` does today on a zero baseline:
  `needs-review` for any increase.

#### Variant rules: two layers

1. **Tag rules.** `check: { fail: { tag: [error] } }` judges the candidate's tag.
   `versus: { fail: { transition: [{ from: success, to: error }] } }` judges the pair; `from` and `to`
   accept `*`. A transition that no rule lists (such as `error → success`, an improvement) is recorded as
   a change and passes.
2. **Field rules, only when both sides share a tag.** Rules under `when.<tag>.<field>` compare a success
   to a success, never an error's time to a success's time. A tag change suppresses field comparison for
   that key; the tag rules alone judge it.

### 5. Test results and the status vocabulary

`diffra/test-results@1` is a `map` keyed by test ID whose values are this variant:

| Tag | Fields |
| --- | --- |
| `success` | `duration` |
| `error` | `duration`, `kind` (`assertion`, `exception`, `timeout`), `message` (`text`) |
| `skipped` | `reason` (`text`) |

The tags deliberately avoid ADR 0004's words. ADR 0004 statuses are **verdicts** about evidence and
policy (`pass`, `fail`, `invalid`, `inconclusive`). Test tags are **observations**. A test in `error`
is a successfully *produced* observation; it does not make the Evidence `invalid`, and its Assessment
outcome is decided by rules. Reusing `pass`/`fail` as tags would put two meanings on one word in the same
report.

Converter mappings: JUnit `failure` → `error` with `kind: assertion`; JUnit `error` → `error` with
`kind: exception`; TRX `Failed` → `error` (kind from the exception type); TRX `Timeout` → `error` with
`kind: timeout`; TRX `NotExecuted`, `Inconclusive` and JUnit `skipped` → `skipped` with a reason. A test
that was discovered but never reported has no tag at all: it is a `missing` key in the collection root
(section 8), never a variant member.

### 6. Stratification and methodology

These are now separate:

- **Methodology** answers "may these two observations be compared at all?" It is the standard type and
  version, the converter ID and version, the converter's input format and version, and (for custom
  metrics) the emitter's methodology string. Any difference is `incompatible`.
- **Stratification** answers "which history does this observation belong to?" It is a projection of the
  run's dimension values onto the dimensions a metric names in `stratify`. Two observations in different
  strata are not compared; nothing is `incompatible`, they simply never meet.

Dimensions are declared once:

```yaml
dimensions:
  os: { values: [linux] }
  arch: { values: [x64] }
  runtime: { values: [net8.0] }
  cpu: { observed: true }
```

- A **planned** dimension lists its values. The planned cross product is the set of strata a complete
  collection must cover (section 8).
- An **observed** dimension is recorded but not planned. GitHub assigns runner CPU models at random, so
  `cpu` cannot be part of the plan, yet GC counts and time must be stratified by it.
- `os`, `arch`, `runtime` and `cpu` are standard dimensions that every standard converter records. A
  project that does not declare one gets it as an observed dimension. Custom dimensions must be declared.

`stratify` is per metric (or per field of a standard type). For #752 this gives allocations one stratum
per `[os, arch, runtime]`, comparable across CPU models, while GC and time also split by `cpu`.

A consequence worth stating: a metric stratified by an observed dimension may find no baseline in its
stratum. A PR that lands on an Intel runner when main only ever ran on AMD gets `inconclusive` for GC and
time, which is honest. Projects that need those gates either pin a runner or rely on `series`.

### 7. Versus, series and the series index

#### Series index

- The series index holds one record per **collected commit × stratum**, ordered by the primary branch's
  first-parent history. Each record points at a collection root (section 8).
- A window is counted in commits, with a maximum age: `window: { commits: 30, maxAge: 90d }`.
- **A PR's versus baseline** for one metric is the newest record in the metric's stratum at or before the
  PR's merge base, among records whose collection root was produced by trusted automation on the primary
  branch. This answers "which baseline?" without a committed baseline file, and resolves to an exact
  commit and Evidence ID as ADR 0003 requires. If the newest record in the stratum is older than
  `maxAge`, or no record exists, versus rules are `inconclusive`.

#### Series rules

```yaml
series:
  window: { commits: 30, maxAge: 90d }
  minPoints: 10
  reference: median
  detect: change-point
  warn: { increase: { above: 10% } }
```

- `increase` / `decrease` compare the candidate with the window's `reference` statistic (`median` or
  `mean`) using the same grammar as `versus`.
- **Small series.** With fewer than `minPoints` records in the window, every series rule is
  `inconclusive`, never `pass`.
- **Severity.** Series rules may be `warn` or `needs-review` in v1, not `fail`. A verdict from summaries
  can be checked but not recomputed from the bundle alone (below), and change-point detection on CI
  runners is not yet proven. Whether series may ever fail a build is an open question.
- `detect: change-point` and `suggest: { factor: k }` are seams. Change-point detection flags keys whose
  raw points go into the bundle. Budget suggestion proposes `versus` thresholds of `k ×` the worst
  same-commit spread in the window, mirroring how #752's budgets were derived by hand. Neither is built
  by this ADR.

#### Retention and storage

- The store holds the raw window as columnar rows (`commit, stratum, metric, key, value`), dictionary- and
  run-length-encoded; most values repeat between commits.
- Points older than the window are downsampled to per-key summaries, then evicted after a retention
  period. Eviction never removes a record that a trusted baseline reference or an open promotion cites.

#### Bundle contents

A bundle never carries the window. It carries:

- the versus inputs: candidate Evidence, baseline reference and baseline Evidence;
- the series verdict;
- the Evidence IDs of every window record;
- per key, O(1) summary statistics: count, mean, variance, min, max and a quantile sketch;
- raw points only for keys a change-point rule flagged.

**A series verdict built from summaries can be *checked* against the store's digests, not fully
*recomputed* from the bundle alone.** A reviewer with the bundle can confirm which records the verdict
used; to recompute it they need the store.

`timeseries-v1` (ADR 0007) is unchanged. It remains a rendering projection for one metric, stratum and
key, and its JSON number values are acceptable because it is not an identity-bearing document. This ADR
amends ADR 0007 on one point: a series is sourced from the series index of collection roots, not from the
sequence of promoted baselines. A future `timeseries-v2` adds `stratum`, `key` and per-point Evidence IDs
when charts need to cite their inputs.

### 8. The collection root: `diffra.collection.v1`

One collection root per subject commit, validated against
[`schemas/collection-v1.schema.json`](../../schemas/collection-v1.schema.json). It is the root that a
bundle, a versus Assessment and a series record all hang off.

It records the subject, the project file's digest and where it was loaded from, and for every declared
metric (each field, for a standard type) × planned stratum an entry with:

- `stratum`: the run's dimension values projected onto that metric's `stratify` list, so observed values
  such as `cpu` appear only where they partition history;

- `status`: `collected`, `missing`, `failed` or `not-applicable` (with a `reason` for every status but
  `collected`);
- the Evidence ID, when collected or failed;
- for `map` metrics, the status of every **expected key**.

**Expected keys come from the producer's plan, not the baseline.** Standard converters report what they
discovered as well as what they measured: BenchmarkDotNet lists planned benchmark cases, TRX lists test
definitions, JUnit lists test cases. A planned key with no measurement is `missing`; one the tool
reported as broken is `failed`. A key present in the baseline but absent from the plan is a *removed
key*, which is policy (`keys.removed`), not incompleteness. That separates "the runner crashed" from "the
benchmark was deleted".

Standard types define completeness, so projects get it without writing it:

| Type | Complete when |
| --- | --- |
| `diffra/test-results@1` | Every discovered test has a result |
| `diffra/benchmark@1` | Every planned case has every field (allocations need the memory diagnoser) |
| `diffra/coverage@1` | The report parsed and its summary has a non-zero line total |
| `diffra/findings@1` | Every SARIF run reports `executionSuccessful: true` |
| `diffra/size@1` | The glob matched at least one file |

The root's `status` is `complete` only when every required metric's every planned stratum is `collected`
or `not-applicable`. The schema enforces this: a root that claims `complete` over a `missing` entry is
invalid. Optional metrics (`required: false`) may be missing from a complete root.

- **`eval` refuses incomplete collections.** Every Assessment over an incomplete root is `inconclusive`.
- **Zero metrics is an error, not a pass.** A root must list at least one metric and at least one
  collected entry; the schema rejects anything else. This generalises #752's refusal to write an empty
  payload.
- **Assembly.** CI matrix legs each upload their payloads; one final job, after all legs, builds the
  collection root. A leg that never ran shows up as a `missing` planned stratum.
- **Identity.** The root has an `id` over every field except `id`, framed as kind `collection`, like
  every other document. Evidence never points at the root, so Evidence identity is unchanged.
- `assessment-v1` does not yet cite the collection root. Until a later Assessment version adds a
  `collectionId`, the bundle carries the root beside the Assessment, and `eval` records the refusal reason
  in its finding.

### 9. Limits

`limits` caps keys per metric per stratum, planned strata, series window length and text length.
Defaults: 10000 keys, 64 strata, 500 window commits, 4096 characters. **Exceeding a limit is a
misconfiguration and fails closed:** collection is `failed` for that entry with the limit named. Diffra
never silently truncates a map, a window or a message.

### 10. Policy trust

The project file is policy, so a pull request that edits it could loosen its own gate. The gate always
loads `diffra.project.yml` **from the trusted base** (the merge-base commit), exactly as baselines are
resolved in [trust and identity](../trust-and-identity.md). The candidate's copy is still schema-validated,
and a difference between the two is reported as a `needs-review` finding naming the changed rules.

Emitters validate against the *candidate's* project file, so a pull request can add a metric and collect
it. The gate judges only metrics the base declares; new ones are reported, not gated, until the change
merges.

### 11. Canonical encodings

ADR 0001 accepts only integer number tokens and strings. Each type has exactly one encoding, or payload
digests stop being stable. **Under a project file, every typed scalar is a JSON string**; bare JSON
numbers remain accepted only in legacy payloads with no project file.

| Type | Encoding (regular expression) | Example |
| --- | --- | --- |
| `count`, `bytes` | `^(0\|[1-9][0-9]*)$` | `"4096"` |
| `integer` | `^(0\|-?[1-9][0-9]*)$` | `"-3"` |
| `decimal` | `^(0\|-?(0\|[1-9][0-9]*)\.[0-9]*[1-9]\|-?[1-9][0-9]*)$` (no `-0`, no trailing zeros) | `"12.5"` |
| `duration` | Non-negative `decimal`, in nanoseconds | `"1250.75"` |
| `ratio` | `^(0\|0\.[0-9]*[1-9]\|1)$` | `"0.8125"` |
| `datetime` | `^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]*[1-9])?Z$`, UTC | `"2026-11-06T09:30:00Z"` |
| `enum` | A declared member, exactly | `"server"` |
| `digest` | `^sha256:[0-9a-f]{64}$` | |
| `text` | The converter-normalised string, unmodified | |
| `variant` | Object with `tag` and that tag's fields only | `{"tag": "success", "duration": "1500000"}` |

Collections encode as in section 3. JCS key ordering makes map encodings canonical automatically.

### 12. The metrics payload

The payload keeps its shape: `scope`, `methodology` and `metrics`, with measurements of the form
`{ "value": ..., "unit": ... }`. **The payload stays schema-less**: the project file assigns types by
metric path. Under a project file it changes in three ways:

- it gains a `stratum` object holding the run's value for every planned and standard dimension;
- it gains a `plan` object listing, per keyed metric, the keys the producer discovered and the ones it
  reported as failed, so the collection root can classify every expected key;
- `methodology` names the converter or emitter identity only (section 6), not dimension values;
- values use the canonical encodings above. `unit` is required on numeric measurements and must equal the
  declared or type-fixed unit exactly. Variant values carry no `unit`; their
  fields' units come from the type.

A standard type's fields appear as groups under the metric: `/benchmarks/allocatedBytes/<case>`.
`evidence-v1` is unchanged: its `payload` is unconstrained, and the payload's validity is defined by the
project file. The `collect` command and the CLI payload reference change when this is implemented.

### 13. Emitter contract

An emitter is any tool that writes a payload: a standard converter or a project's own script. Standard
converters guarantee, and custom emitters must:

1. **Validate before writing.** Load the project file, derive the payload schema for the metrics it
   emits (allowed metric and field names, encodings, units, variant tag fields) and validate the payload
   in memory. An invalid payload is never written. The worked example ships one such derived schema.
2. **Emit only declared metrics**, with the declared type and unit. An undeclared metric, a wrong unit or
   a field outside its variant tag is an emitter error, not a Diffra finding.
3. **Report the plan with the measurements**: discovered keys, and which of them failed, so the collection
   root can tell `missing` from `failed` from removed.
4. **Refuse to emit nothing.** A payload with zero measurements is an error: exit non-zero and write no
   file.
5. **Be deterministic.** Same input bytes and converter version give the same payload bytes: stable key
   derivation, normalised paths (ADR 0005, ADR 0006), normalised text, no timestamps, hostnames or temporary
   paths.
6. **Record identity**: converter ID and version, input format and version, and the standard dimension
   values, in `methodology` and `stratum`.

### 14. Coverage: a converted source, not a parallel model

ADR 0006's normalisation (repository-relative paths, ordinal ordering, no timestamps) becomes the first
stage of the `cobertura` and `opencover` converters. `coverage-v1` stays as the converter's normalised
intermediate artifact and is referenced from the Evidence's `artifactRefs`. Its typed outputs are
`diffra/coverage@1`'s fields. ADR 0006's policies map as follows:

| ADR 0006 policy | Project file |
| --- | --- |
| `minOverallLineCoverage` | `fields.lineRate.check.fail.below` |
| `maxOverallCoverageDrop` | `fields.lineRate.versus.fail.decrease.above` |
| `failOnNewlyUncoveredLines` | Unchanged: the coverage comparator. It needs Git diff hunks and `uncoveredLineRanges`, which are not metrics. |

One drift is recorded, not fixed here: `coverage-v1` encodes `rate` as a JSON number, which ADR 0001
rejects when it is fractional. `ratio` is the canonical form; a `coverage-v2` should adopt it.

## Consequences

- One project file replaces #752's three payloads and three rules files, and expresses its "> 4 KB and
  > 5%" gate directly.
- Adopters using standard types write a few lines per tool. Custom types remain for everything else.
- Completeness is checked by Diffra, not re-implemented by every adopter.
- Changing a converter or standard type version resets history for that metric; this is the price of
  `incompatible` being honest.
- The schema can validate standard-type overrides fully, but custom composite field rules only in shape;
  the loader carries the rest.
- Not built here: the loader, converters, the series engine, change-point detection, budget suggestion,
  charts and an emitter SDK.
