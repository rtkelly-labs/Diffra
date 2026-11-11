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
   types are built before the first adopter installs Diffra. Converters are tested against
   published validators for each format and against synthetic values.
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

## Not yet decided

- Whether "all standard dataset types" includes flame graphs, Parquet input and OpenMetrics
  input.
- Whether each supported tool version also needs one real output file as a test fixture
  (proposed).
- Whether size data folds into the generic keyed table type.
- How a comparison against the previous release is expressed.
- What the output says when an optional dataset is absent for the whole history window, and what
  an occasionally collected dataset is compared with.
- The name of the collecting workflow, given that `collect` is already a CLI operation.

## Current code

`schemas/root-file-v1.schema.json` and `examples/root-file/` describe the root file in this
record: settings, datasets with named rule sets, `required: false`, and projects that map each
dataset to a rule set. The schema does not check that a project names a rule set or dataset that
exists; the loader will. The file's name in a repository is not decided. No code loads the root
file and `diffra record` does not exist.

`schemas/project-v1.schema.json`, `schemas/collection-v1.schema.json` and
`examples/project-file/` describe the earlier single-project, metric-based design. They are kept
because the root file reuses their rule shapes and the collection root has no replacement yet.
