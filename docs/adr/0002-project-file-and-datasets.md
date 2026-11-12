# 0002: The project file and datasets

- Status: accepted
- Date: 2026-11-11

## Context

An adopter must tell Diffra what its CI produces, how to read it, and which rules apply. The
earlier design had one project per file, built around "metrics", and asked every emitter to load
and validate the project file itself. That duplicates work in every language, and a broken
emitter's error never reached the pull request.

## Decision

1. **One YAML file at the repository root.** It can define several projects, datasets and
   named rule sets. There is no `.diffra/` folder and no file merging.
2. **A dataset is one collected signal** with its type and source. It is collected and stored
   once and has one history.
3. **Rules sit on the dataset.** A dataset can carry several named rule sets. A project is a
   named selection of datasets and chooses which rule set applies, so a release project can
   judge the same data more strictly than a main-branch project without storing it twice. Two
   datasets are needed only when the measurements themselves differ.
4. **Optional is a property of the dataset** (`required: false`), not of one part of it.
5. **Diffra reads each tool's own output.** A built-in converter turns a tool's standard output
   into a typed dataset, so the adopter does not reshape output for Diffra. All standard dataset
   types are built before the first adopter installs Diffra, including flame graphs and Parquet
   and OpenMetrics input. Converters are tested against published validators for each format and
   against synthetic values.
6. **CI timing and the CI job graph are datasets from the first install.** Each run records wait
   time, queue time and execution time per job and for the longest path through the job graph.
   Rules apply to execution time only. A re-run uses the latest attempt and notes that earlier
   attempts exist.
7. **Limits are settings with defaults.** Safety limits (bytes read, archive expansion, nesting
   depth, path escape) stay hard, and a rejected input is saved so it can be reported. Size
   limits (number of keys, text length) are budgets: totals and the largest entries are kept and
   the truncation is stated in one line.
8. **`diffra record` is the one command each data-producing job runs.** The CLI validates every
   value against the project file at once. An invalid value is saved as a rejected entry and
   `record` still exits 0, so the final step reports every problem together. A final step checks
   that every required dataset was collected.
9. **A pull request is judged by its own copy of the project file.** A change to the rules is
   shown in the output, not hidden until after merge.
10. **Storage is the only strategy at first.** It is set once per repository and shared by all
    projects. A strategy is a swappable backend and never changes a result.
11. **Tables declare their columns.** A table dataset declares each column's type, unit and
    better direction, and a converter supplies defaults for the formats it reads. Size data stays
    a separate dataset type.
12. **An absent optional dataset is quiet.** When an optional dataset has no record in the whole
    history window, the full report says so in one line and the comment says nothing. A dataset
    collected only occasionally is compared with its most recent record on the target branch, and
    the output names that commit and how far back it is.
13. **There is no comparison with the previous release for now.** A pull request is compared with
    the commit it branched from and with recent history only.

## Not yet decided

- Whether each supported tool version also needs one real output file as a test fixture
  (proposed).
- The name of the collecting workflow, given that `collect` is already a CLI operation.
- What visual comparison covers, and whether a dataset given only with `--value` declares a type
  or shape in the root file. [Dataset types and converters](../datasets.md#not-yet-decided) lists
  the other open points about types and converters.

## Current code

`schemas/root-file-v1.schema.json` and `examples/root-file/` describe the root file in this
record: settings, datasets with named rule sets, `required: false`, and projects that map each
dataset to a rule set, or to `null` to record it without rules. The file's name in a repository
is not decided. The standard dataset types are those of the earlier project file
(`diffra/benchmark@1`, `diffra/size@1` and others); there is no table type with declared columns
(decision 11), and CI timing and the job graph (decision 6) have no type yet.
[Dataset types and converters](../datasets.md) specifies the standard types, with
`diffra/table@1` in place of `diffra/benchmark@1`, and proposes each one's shape, identity,
converters, comparison and tests; none of it is built. The Baseline reference and bundle
manifest schemas still allow a `previous-release` role (decision 13).

`src/RootFile/` loads the root file. It checks what the schema cannot: that each dataset a
project selects is declared, that each rule set a project applies is defined on that dataset,
that a custom dataset type is declared under `types`, and that each `stratify` entry is declared
under `dimensions`. It reads datasets, rule-set names, units, sources, whether a dataset is keyed,
the dimensions each is stratified by, the planned values of each dimension, projects and settings, and accepts a small
part of YAML (one document, no anchors, aliases or tags, no duplicate keys). It does not validate
the file against the schema, so rule bodies are checked only by the schema. [Recording a run](../record.md) specifies `diffra record` (decision 8) and
`schemas/record-entry-v1.schema.json` the entries it writes. Both cover a `--value` and the
dataset's `from` source, with `from` preferred. `src/Cli/RecordCommand.cs` is the command: it
loads the root file and records a `--value`, a skipped or a failed dataset, and saves every
problem as a rejected entry. No converter exists, so `from` cannot be read yet, and the schema
still requires `from` on every dataset. `src/Protocol/Recording.cs` validates an entry against the
schema's rules, appends entries to and reads an entries file, refuses a different value for the
same job and attempt unless it replaces, and merges the entries of every job by the proposed
rules; no collecting job calls the merge yet.

`schemas/project-v1.schema.json`, `schemas/collection-v1.schema.json` and
`examples/project-file/` describe the earlier single-project, metric-based design. The root
schema copies their rule shapes rather than referring to them. They are kept because the
collection root has no replacement yet.
