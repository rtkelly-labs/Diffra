# Dataset types and converters

This page specifies the standard dataset types and the converters that fill them: what each type
holds in a record entry, what its items are keyed by, which formats it reads, how two records of
it are compared, its limits, and how its converters are tested. None of it is built. The root
file schema still lists the earlier types (`diffra/benchmark@1`, `diffra/coverage@1`,
`diffra/findings@1`, `diffra/size@1` and `diffra/test-results@1`), and `diffra record` cannot
yet read the files a dataset's `from` names ([Recording a run](record.md)).

## What is decided

These points are decided in the decision records:

- Diffra reads each tool's own output. A built-in converter turns a tool's standard output into a
  typed dataset, so the adopter does not reshape output for Diffra
  ([0002](adr/0002-project-file-and-datasets.md) decision 5).
- All standard dataset types are built before the first adopter installs Diffra, including flame
  graphs and Parquet and OpenMetrics input (0002 decision 5).
- Converters are tested against the published validator for each format, where one exists, and
  against synthetic values (0002 decision 5).
- CI timing and the job graph are datasets from the first install. Each run records wait, queue
  and execution time per job and for the longest path through the job graph. Rules apply to
  execution time only. A re-run uses the latest attempt and notes that earlier attempts exist
  (0002 decision 6).
- Safety limits (bytes read, archive expansion, nesting depth, path escape) are hard, and a
  rejected input is saved so it can be reported. Size limits are budgets: totals and the largest
  entries are kept and the truncation is stated in one line (0002 decision 7).
- A table dataset declares each column's type, unit and better direction, and a converter
  supplies defaults for the formats it reads. Size data stays a separate dataset type (0002
  decision 11).
- Binary files are stored once per hash, with an index from hash to the run and artifact that
  hold them. A record refers to a file by hash, size and media type
  ([0004](adr/0004-records-integrity-and-storage.md) decision 5).
- A dataset names its converter in the root file. Diffra never picks a converter from a file
  extension or from the shape of the content
  ([0009](adr/0009-dependencies-and-extensions.md) decision 4).
- Parquet is read by a separate converter package built from this repository on Parquet.Net,
  which stays out of the core (0009 decision 7). Optional converters are separate .NET programs
  outside the native CI build ([0007](adr/0007-packaging-providers-and-the-action.md) decision 5).

These points are also decided, and are not yet written into a decision record:

- The standard dataset types are test results, findings, coverage, a generic table, size, CI
  timing and the job graph, files with their hashes, and flame graphs, with Parquet and
  OpenMetrics as inputs.
- Converters for tool-specific formats are built into Diffra and released with it. They are not
  separately versioned packages. Parquet is the one exception.
- `diffra/table@1` replaces `diffra/benchmark@1`. Its rows are keyed by case. Its columns are
  declared in the root file, each with a type, a unit and a better direction. Each converter
  supplies default columns for the format it reads, and BenchmarkDotNet's JSON is the first.
- `diffra/flamegraph@1` reads collapsed (folded) stacks as text. It is stored as a keyed table
  from stack to sample count, and compared per stack and per frame.
- OpenMetrics text is read by a converter in the core.
- Any CI artifact is in scope, including binary files.
- Judging thresholds in natural units, such as bytes and durations, is a direction only and is not
  built. The root file schema already accepts thresholds written with a unit, such as `10KiB` or
  `2ms`, but nothing evaluates them yet.

Everything else on this page is *proposed*.

## Common rules (proposed)

### Where converters live

The built-in converters live in one library, `src/Converters/`, which the CLI references. They use
only the .NET base library (`System.Text.Json` and `System.Xml`), so they add no package
references, and they run under the CI build as it is compiled. The protocol library stays free of
references and holds the type definitions, not the readers.

The Parquet converter is a separate .NET program built from this repository, `diffra-parquet`,
with its own package and its own entry in `.config/package-references.json`. How `record` finds
and runs it is not yet decided.

The OpenMetrics text format needs no library: its grammar is line-based and small. Its protobuf
form would need one and is out of scope.

### The shape of `data`

An entry's `data` keeps the nesting the entry examples already use: field, then key, then an
object holding `value`.

```json
{ "results": { "Suite.Case": { "value": { "tag": "success", "duration": "1532000" } } } }
```

- A field that is not keyed holds `{ "value": ... }` directly, such as `"total": { "value": "4" }`.
- Numbers are canonical decimal strings, as in
  [Recording a run](record.md#the-entries-file-proposed). Bytes are in `B` and counts have no
  unit. Durations are in nanoseconds unless a table column declares another unit. A value is
  written in the unit its field or column declares, so no `unit` is written beside it.
- Converters do arithmetic in decimals, never in binary floating point, so a value converts the
  same way on every platform. A source number that cannot be held exactly, such as a float with
  more significant digits than the declared column allows, is rounded half to even to 9 decimal
  places, and the rounding is the converter's, not the platform's.
- Keys and text are valid Unicode with no control characters. A path in `data` follows the rules
  for a source path in an entry: relative to the repository root, forward slashes, no `.` or `..`
  segment. A path a tool wrote as absolute is made relative by removing the repository root; a
  path outside the repository is kept under the key `<outside>/<file name>`, and the entry notes it.

### Converter names and versions

A converter name is the key under `from` in the root file, as today: `from: { junit: "..." }`.
Each entry's `sources` already records the path, converter and SHA-256 digest of every file read.
It is proposed that each source also records the format version the converter detected, such as
`2.1.0` for SARIF, which is a change to the entry schema. A converter that meets a format version
it does not support rejects the entry and says which versions it reads.

### Determinism

The same input files give the same `data`, byte for byte, on every platform and culture. Files
matched by a glob are read in ordinal path order. Keys are written in ordinal order. Nothing in
`data` depends on the time, the machine or the order in which files were found.

### Untrusted input

Every file a converter reads is untrusted, even when it came from the adopter's own job.

- A file is read only when it lies inside the repository, with links not followed out of it. A
  file over 64 MiB, or files that together pass 256 MiB in one entry, are not read and the entry
  is rejected. The 1,000-source cap in the entry schema applies.
- XML is read with DTD processing prohibited, no external entities and no resolver, nesting at
  most 256 deep, and no more than 10 million characters in one attribute or text node.
- JSON is read with a nesting limit of 256 and no comments or trailing commas, unless the format's
  own tools write them.
- Text formats limit one line to 1 MiB.
- Archives, when a converter reads one, are expanded at most 100 times their stored size and to
  at most 1 GiB, and a member whose path escapes the archive is rejected.
- A converter never runs code, follows a URL or opens a network connection from what it reads.

These are safety limits. Exceeding one rejects the entry with a problem that says which limit,
and the numbers above are proposals.

### Budgets

The root file's `limits` settings are budgets: `maxKeys` caps the number of keys in one keyed
field, and `maxTextLength` caps one text value such as a failure message. When a field has more
keys than the budget, the converter keeps the totals exact, keeps the keys that matter most for
that type (named below), and records one line in `data` under `truncated`, saying how many keys
were dropped from which field. Text over `maxTextLength` is cut and ends with `…`.

### Datasets split across jobs

The merge rules compare an entry's whole `data` for one project, dataset and dimensions
([Recording a run](record.md#merging-the-jobs-entries-proposed)). Two jobs that each hold part of
one dataset, such as sharded tests or several benchmark jobs, therefore conflict unless they
differ in a dimension. Until a merge by key is decided, an adopter who splits a dataset across
jobs stratifies it by an observed dimension such as `shard`. A merge that takes the union of keys
from several jobs, and reports a key that two jobs give differently, is proposed for every keyed
type below; it changes the merge rules and is listed under "Not yet decided".

### Comparison

Items are matched by key between a pull request's record and the record at its comparison point.
A key present on one side only is added or removed, and the `keys` rules (`added`, `removed`)
judge it. A matched key is compared field by field with the rule types the root file already
has. Fields that are noisy from run to run are judged against the history window
([0003](adr/0003-comparison-history-and-noise.md)).

## Test results: `diffra/test-results@1`

### What it holds

| Field | Key | Value |
| --- | --- | --- |
| `results` | Test | One of `{ "tag": "success", "duration" }`, `{ "tag": "error", "duration", "kind", "message" }` or `{ "tag": "skipped", "reason" }`. `duration` is in nanoseconds. `kind` is `assertion`, `exception` or `timeout`. |
| `total`, `succeeded`, `failed`, `skipped` | — | Counts. |
| `duration` | — | The sum of every test's duration, in nanoseconds. |

The root file schema has only `results` today; the other fields, and `occurrences` below, are new.

### Identity

A test is keyed by its fully qualified name: for JUnit, `classname` and `name` joined by `.`, or
the enclosing `testsuite` name and `name` when there is no `classname`; for TRX, the test method's
`className` and the result's `testName`, which carries the arguments of a data-driven test. A key
that occurs more than once in one entry, as when a tool re-runs a flaky test, is kept once with
the worst outcome (`error`, then `success`, then `skipped`) and gains `occurrences`, the number of
times it ran.

### Converters

| Converter | Format | Versions |
| --- | --- | --- |
| `junit` | JUnit XML as written by Surefire, pytest, Vitest and most other runners | The common `testsuites`/`testsuite`/`testcase` shape; `time` in seconds |
| `trx` | Visual Studio test results | The namespace that `dotnet test` writes (the 2010 namespace, to confirm) |

### Default fields

All of the fields above. `kind` comes from the failure element: `failure` is `assertion`, `error`
is `exception`, and a TRX outcome of `Timeout` is `timeout`.

### Comparison

Per test, a change of tag is a transition (`success` to `error`, for instance), which the existing
`transition` rules judge. Duration is compared only when both sides succeeded, and against the
history window, since it is noisy. Added and removed tests are judged by the `keys` rules.

### Limits and budgets

`maxKeys` caps `results`; failed tests are kept first, then the slowest. Messages are capped by
`maxTextLength`. The counts and total duration stay exact.

### Tests

There is no single published JUnit schema; the converter is tested against the JUnit XML schemas
that test-reporting tools publish (which ones is to confirm) and against synthetic files covering
every outcome, nested suites, missing `classname`, repeated keys and malformed input. TRX is
tested against its published schema, `vstst.xsd`, if its licence allows the tests to use it (to
confirm), and synthetic files.

## Findings: `diffra/findings@1`

### What it holds

| Field | Key | Value |
| --- | --- | --- |
| `results` | Finding | `{ "tool", "rule", "level", "path", "line", "message" }`. `level` is `error`, `warning`, `note` or `none`; `line` is the start line, or absent. |
| `byRule` | Tool and rule, as `<tool>/<rule>` | Count of findings. |
| `total` | — | Count of findings. |

`results` is a new field; the root file schema has only `total` and `byRule` today.

### Identity

A finding is keyed by tool, rule, path and a fingerprint, so that a finding moved by an edit
above it stays the same finding. The fingerprint is the first of: a SARIF `fingerprints` value, a
`partialFingerprints` value, or a SHA-256 of the message text with digits removed. The line is
data, not identity. Findings that share a key, such as the same message twice in one file, are
kept once with `occurrences`.

### Converters

| Converter | Format | Versions |
| --- | --- | --- |
| `sarif` | SARIF, as CodeQL, Biome (from 2.4) and Ruff (`--output-format sarif`; the first version is to confirm) write it | 2.1.0 |

### Default fields

All of the fields above. The tool is the run's `tool.driver.name`; the level is the result's
`level`, or the rule's default, or `warning`, as SARIF specifies.

### Comparison

Added and removed findings are judged by the `keys` rules, which is how a pull request is told
about the findings it introduced. `total` and each `byRule` count are compared as counts.

### Limits and budgets

`maxKeys` caps `results`; errors are kept first, then warnings, then the rest. `total` and
`byRule` stay exact.

### Tests

Against the OASIS SARIF 2.1.0 JSON schema, and synthetic files covering several runs in one log,
missing rule metadata, relative and absolute URIs with `uriBaseId`, and malformed input.

## Coverage: `diffra/coverage@1`

### What it holds

| Field | Key | Value |
| --- | --- | --- |
| `coveredLines`, `totalLines` | — | Counts. |
| `coveredBranches`, `totalBranches` | — | Counts; absent when the format has no branch data. |
| `lineRate`, `branchRate` | — | Ratios from 0 to 1, rounded half to even to 6 decimal places. |
| `fileLineRate`, `fileBranchRate` | Path | Ratios, as above. |

`coveredBranches` and `totalBranches` are new fields.

### Identity

Files are keyed by repository-relative path. Cobertura's `sources` element and the paths in
OpenCover are resolved against the repository root.

### Converters

| Converter | Format | Versions |
| --- | --- | --- |
| `cobertura` | Cobertura XML, as coverlet, coverage.py, Istanbul and Vitest write it | The `coverage-04` document type |
| `opencover` | OpenCover XML, as coverlet and OpenCover write it | The format OpenCover's last release writes (to confirm) |

### Default fields

All of the fields above. When an entry reads several files, as when each test project writes its
own report, lines are combined line by line before counting: a line is covered when any file
covers it, so a line counted in two reports is counted once.

### Comparison

Rates and counts are compared as numbers; coverage is deterministic, so fixed thresholds apply.
A file present on one side only is added or removed.

### Limits and budgets

`maxKeys` caps the per-file fields; files with the most uncovered lines are kept first. The
totals stay exact.

### Tests

Cobertura is tested against its published document type, `coverage-04.dtd`, by a test that loads
the DTD from the test's own files, never from the network. OpenCover has no published schema
that is known; it is tested with synthetic files. Both cover empty reports, files outside the
repository and reports that overlap.

## Tables: `diffra/table@1`

### What it holds

| Field | Key | Value |
| --- | --- | --- |
| One field per column | Case | The column's value, in its declared unit. A case that has no value for a column has no key in that column. |

The root file declares the columns:

```yaml
benchmarks:
  type: diffra/table@1
  from: { benchmarkdotnet: "artifacts/benchmarks/*-report-full.json" }
  columns:
    meanTime: { better: lower }
    allocatedBytes: { better: lower }
    medianTime: { source: Statistics.Median, type: duration, unit: ns, better: lower }
```

A column has a `type` (`count`, `integer`, `decimal`, `bytes`, `duration` or `ratio`), a `unit`
where the type takes one, and `better`: `lower`, `higher` or `none`. `none` means a change is
shown without being called better or worse. When `columns` is left out, the converter's default
columns apply. When it is given, it selects columns: a default column listed by name keeps its
default type and unit, and may set `better`. Any other column also names its `source`, the field,
metric or column it is read from, and must give `type` and `unit`.

### Identity

Rows are keyed by case: a benchmark's full name with its parameters, an OpenMetrics label set, or
the value of the key column a Parquet dataset declares. A case that occurs twice in one entry is
rejected, because the converter cannot tell which value is right.

### Converters

| Converter | Format | Versions | Package |
| --- | --- | --- | --- |
| `benchmarkdotnet` | BenchmarkDotNet's JSON exporter, brief or full | The exporter's output from 0.13 onwards (to confirm) | Core |
| `openmetrics` | OpenMetrics text exposition | 1.0 | Core |
| `parquet` | Apache Parquet files | Those Parquet.Net reads | `diffra-parquet` |

### Default columns

| Converter | Column | Type and unit | Better | Read from |
| --- | --- | --- | --- | --- |
| `benchmarkdotnet` | `meanTime` | duration, `ns` | lower | `Statistics.Mean` |
| | `allocatedBytes` | bytes | lower | `Memory.BytesAllocatedPerOperation` |
| | `gen0PerKOps`, `gen1PerKOps`, `gen2PerKOps` | count, `count/1000-ops` | lower | `Memory.Gen0Collections` (and so on) per 1,000 of `Memory.TotalOperations` |
| `openmetrics` | One per gauge or counter family | decimal, with the family's `UNIT` if it has one | none | The sample's value; a counter's `_total` sample |
| `parquet` | None | — | — | Columns must be declared |

The BenchmarkDotNet field names above are to confirm against the exporter's current output, and
match the earlier `diffra/benchmark@1` fields and units. The BenchmarkDotNet case is the
benchmark's `FullName`. One file can hold the same benchmark under several jobs, such as two
runtimes; whether the job then becomes part of the case or a dimension is open.

An OpenMetrics row is the sample's label set, written in label-name order as `name="value"` pairs
joined by `,`; an empty label set is the case `{}`. Histogram and summary families have no default
column; a declared column may name one of their samples, such as `request_seconds_sum`.

### Comparison

Per case and column, by the column's type. `better` gives the direction of a change, so that a
rule written as `worse: { above: 5% }` means an increase for a `lower` column and a decrease for a
`higher` one; that rule spelling is proposed. Timing columns are noisy and are judged against the
history window.

### Limits and budgets

`maxKeys` caps the cases in each column; the cases with the largest change against the comparison
point cannot be known when recording, so the converter keeps cases in key order and states the
truncation. The number of declared columns is at most 64. Parquet is limited to 1 million rows
and 256 MiB after decompression.

### Tests

BenchmarkDotNet publishes no schema for its JSON; the converter is tested with synthetic files
covering brief and full exports, parameterised benchmarks, missing memory diagnostics and
benchmarks that failed. OpenMetrics is tested against the OpenMetrics project's parser test cases
(how each case states whether its input is valid is to confirm), and synthetic files. Parquet is
tested against the Apache `parquet-testing` data files, and synthetic files covering each column
type the converter accepts.

## Size: `diffra/size@1`

### What it holds

| Field | Key | Value |
| --- | --- | --- |
| `total` | — | Bytes. |
| `files` | Path | Bytes. |

### Identity

Files are keyed by repository-relative path.

### Converters

| Converter | Format | Versions |
| --- | --- | --- |
| `files` | Any files a glob matches; their length on disk | — |

### Default fields

Both. Reading the members of an archive, such as the files inside a package, is not proposed yet.

### Comparison

Sizes are deterministic and compared with fixed thresholds in bytes.

### Limits and budgets

`maxKeys` caps `files`; the largest files are kept first. `total` stays exact.

### Tests

There is no format to validate. Synthetic folders cover empty files, links (not followed), files
outside the repository and globs that match nothing.

## CI timing and the job graph: `diffra/ci-timing@1`

The type name is proposed. One type holds both, because the longest path is measured over the
graph.

### What it holds

| Field | Key | Value |
| --- | --- | --- |
| `jobs` | Job | `{ "queue", "execution", "conclusion", "needs" }`. Durations in nanoseconds; `conclusion` is `success`, `failure`, `cancelled`, `skipped` or `running`; `needs` lists the keys of the jobs it waits for. |
| `wait` | — | Duration from the run's creation to the last job finishing. |
| `queue` | — | The sum of every job's queue time. |
| `pathExecution` | — | Execution time along the longest path through the graph. |
| `path` | — | The keys of the jobs on that path, in order. |
| `attempt`, `earlierAttempts` | — | The attempt measured and how many attempts came before it. |
| `complete` | — | Whether every job had finished when it was read. |

### Identity

Jobs are keyed by the name the CI host shows, which includes matrix values. A job that occurs
twice under one name is kept once with the longer execution time and gains `occurrences`.

### Converters

The core never calls a CI host ([0007](adr/0007-packaging-providers-and-the-action.md) decision
3), so the provider hands the converter a file. The proposal is a provider-neutral JSON file
that the provider writes: the run, its attempt, and each job's name, creation, start and finish
times, conclusion and dependencies. On GitHub, the Action or `diffra-github` writes it from the
jobs API and the workflow file, as `scripts/CiTiming.cs` does today.

| Converter | Format | Versions |
| --- | --- | --- |
| `ci-run` | The provider-neutral run file above (proposed) | 1 |

### Default fields

All of the fields above. Execution counts the time at least one leg of a job was running, so a
matrix counts once on the path.

### Comparison

Rules may judge `pathExecution` and each job's `execution` only. Wait and queue are shown and
never judged. A job present on one side only is added or removed. Execution time is noisy and is
judged against the history window.

### Limits and budgets

`maxKeys` caps `jobs`; jobs on the longest path are kept first, then the longest. At most 1,000
jobs are read.

### Tests

GitHub publishes an OpenAPI description of its REST API; the provider's writer is tested against
the jobs response it describes. The converter is tested with synthetic run files covering
matrices, re-runs, skipped and cancelled jobs, a run still going, and a graph with a cycle, which
is rejected. The graph checks reuse `src/Graph/`, reworked for the CI job graph
([0009](adr/0009-dependencies-and-extensions.md) decision 3).

## Files: `diffra/files@1`

The type name is proposed.

### What it holds

| Field | Key | Value |
| --- | --- | --- |
| `files` | Path | `{ "sha256", "bytes", "mediaType" }`. |

The bytes are stored once per hash, outside the entry, with an index from hash to the run and
artifact that hold them ([0004](adr/0004-records-integrity-and-storage.md) decision 5). Uploading
them is part of what a job hands on; for this type, the raw files are what was asked for
(0004 decision 6).

### Identity

Files are keyed by repository-relative path, or by path inside the artifact when the provider
hands over a CI artifact.

### Converters

| Converter | Format | Versions |
| --- | --- | --- |
| `files` | Any files a glob matches | — |

The media type comes from a fixed table of file extensions in the converter, with
`application/octet-stream` for anything else. The media type is shown in reports; it never picks
a comparison.

### Default fields

`files`, as above.

### Comparison

Per path: unchanged when the hash is equal, otherwise changed, with the change in size. Added and
removed files are judged by the `keys` rules. A report links both versions. Comparing the
contents, such as images, is not decided.

### Limits and budgets

One file is at most 64 MiB and an entry's files at most 256 MiB, as for every converter.
`maxKeys` caps `files`, keeping the largest first. How long stored bytes are kept, and what a
report says when they have expired, is not decided
([0004](adr/0004-records-integrity-and-storage.md)).

### Tests

No format to validate. Synthetic folders cover identical files under two paths (stored once),
empty files and globs that match nothing.

## Flame graphs: `diffra/flamegraph@1`

### What it holds

| Field | Key | Value |
| --- | --- | --- |
| `stacks` | Stack | Sample count. |
| `total` | — | Sample count over every stack. |

Per-frame figures are derived when comparing, not stored.

### Identity

A stack is keyed by its frames joined by `;`, root first, exactly as the input writes them,
including annotations such as `_[k]`. The same stack on two lines is summed.

### Converters

| Converter | Format | Versions |
| --- | --- | --- |
| `collapsed` | Collapsed stacks: one stack per line, frames separated by `;`, then a space and a count | — |

Profilers that write this format, directly or through the FlameGraph collapse scripts, include
`perf`, py-spy and async-profiler. The count is the last space-separated token, so a frame may
contain spaces.

### Default fields

Both.

### Comparison

Run lengths differ, so stacks and frames are compared by their share of `total`, not by raw
counts. Per stack, the share is compared. Per frame, two shares are derived: self (the frame is
the leaf) and total (the frame is anywhere in the stack, counted once per stack when it recurs).
The report ranks frames by the change in their total share. A stack or frame present on one side
only is added or removed.

### Limits and budgets

At most 512 frames in a stack. `maxKeys` caps `stacks`; the stacks with the most samples are
kept, and the rest are summed into one stack keyed `[truncated]`, so `total` and every kept share
stay exact.

### Tests

There is no published validator for collapsed stacks. Synthetic files cover frames with spaces,
empty frames, repeated stacks, counts that are not whole numbers (rejected), and very deep stacks.

## Converters at a glance

| Converter | Type | Format | Package |
| --- | --- | --- | --- |
| `junit` | `diffra/test-results@1` | JUnit XML | Core |
| `trx` | `diffra/test-results@1` | TRX | Core |
| `sarif` | `diffra/findings@1` | SARIF 2.1.0 | Core |
| `cobertura` | `diffra/coverage@1` | Cobertura XML | Core |
| `opencover` | `diffra/coverage@1` | OpenCover XML | Core |
| `benchmarkdotnet` | `diffra/table@1` | BenchmarkDotNet JSON | Core |
| `openmetrics` | `diffra/table@1` | OpenMetrics text | Core |
| `parquet` | `diffra/table@1` | Apache Parquet | `diffra-parquet` |
| `files` | `diffra/size@1`, `diffra/files@1` | Any file | Core |
| `ci-run` | `diffra/ci-timing@1` | The provider's run file | Core |
| `collapsed` | `diffra/flamegraph@1` | Collapsed stacks | Core |

Proposed beyond the decisions: the type names `diffra/ci-timing@1` and `diffra/files@1`, the
converter names `ci-run` and `collapsed`, and Parquet filling `diffra/table@1`. Each converter is
also benchmarked in this repository on real input (see the [plan](../PLAN.md), step 6).

## Not yet decided

- Whether each supported tool version also needs one real output file as a test fixture
  (proposed).
- What visual comparison covers, such as comparing images, and whether it needs an image decoder
  ([0009](adr/0009-dependencies-and-extensions.md)).
- Whether a dataset given only with `--value` declares a type or shape in the root file
  ([Recording a run](record.md#the-command-proposed)).
- Whether the merge takes the union of keys from several jobs for keyed types, so that a dataset
  can be split across jobs without a dimension.
- How `record` finds and runs the separate Parquet program, and how it reports that the program is
  not installed.
- Which job records CI timing, given that the collecting job is still running when it reads the
  run, and whether the collecting job's own time is measured.
- Formats for the rollout's tools that have no route here yet: mypy writes JSON and JUnit XML but
  no SARIF, so its findings need a converter for its JSON or another route; Biome writes SARIF only
  from 2.4; and Vitest's benchmark output has no table converter.
- Whether a BenchmarkDotNet benchmark run under several jobs in one file is told apart by the case
  or by a dimension.
- Whether to read the Prometheus text format as well as OpenMetrics, and whether `size` reads the
  members of archives such as packages.
- Which published JUnit schemas the `junit` converter is tested against, and whether the TRX
  schema may be used in tests.
- Whether the format version each source was read as is added to the entry schema.
