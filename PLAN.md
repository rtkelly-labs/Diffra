# Diffra plan

This file is the active implementation sequence. Read [docs/vision.md](docs/vision.md) for the
goal, the [decision records](docs/adr/) for the design, and the [glossary](docs/glossary.md) for
the words. Do not create GitHub issues or publish packages until the repository owner authorizes
those external actions.

## Goal

Find out whether Diffra is worth using across the owner's repositories by adopting it in them one
at a time. Each adoption must be cheap to wire, fast in CI, and show findings that someone acts
on. The work below is ordered to make the first adoption possible as early as possible.

## Rollout

Diffra is adopted by these public repositories, in this order:

1. [rtkelly-labs/github-actions](https://github.com/rtkelly-labs/github-actions) (Python): one CI
   workflow running Ruff, mypy and pytest. It proves the simple path.
2. [rtkelly-labs/Parquet.SourceGenerator](https://github.com/rtkelly-labs/Parquet.SourceGenerator)
   (C#): many workflow files, including benchmarks, mutation testing and fuzzing. It needs
   collection across workflow files.
3. [rtkelly-labs/mermaid-toolkit](https://github.com/rtkelly-labs/mermaid-toolkit) (TypeScript):
   Vitest with coverage and benchmarks, and Biome for linting. Check what Biome can emit first.

After those three, one installation in a repository the owner does not own, judged by that team,
tests whether the setup works for someone else. Diffra keeps running on itself throughout, but
that does not count as rollout evidence.

### Review triggers

For each adopting repository:

| Condition | Limit |
| --- | --- |
| Wiring | At most 60 lines: one root file, one step per job that produces data, one collecting job. No scripts and no test changes. |
| CI overhead | At most 60 seconds added per pull request, and 15 seconds in any one job. |
| Usefulness, over the first 10 pull requests | At least 1 finding acted on, at most 2 comments judged noise, and no regression found another way that Diffra held the data for. |

These numbers trigger a review; they are not gates. The owner judges repositories 1 to 3. A
failed condition pauses the rollout at that repository until the cause is fixed.

The critical path for the wiring limit is the root file, the built-in converters and the Action.
Until all three exist, no repository can adopt Diffra within the limit.

## Work sequence

Each step is a set of small pull requests, each with tests. Smaller changes go first.

### 1. Clear the earlier design (done)

- The graph executor and builder are deleted. The graph data model and validator are moved out
  of the protocol library, for reuse in recording the CI job graph.
- The self-evaluation baseline search states the distance and the commits in between when it
  falls back to an earlier commit, and says "cannot attribute" when nothing in the window has a
  record.
- The package-reference rule is stated in record 0009 and checked by the protocol tests.

### 2. Settings and output

- Turn hard-coded limits into settings with defaults. Safety limits stay hard; size limits become
  budgets, and truncation is stated.
- Attention levels (`act`, `review`, `quiet`) and the one-screen limits on the comment and the
  agent-facing Markdown, with ranking.
- Optional event and telemetry output as JSON-lines files, off by default.

### 3. The root file and `diffra record`

- The root YAML file: projects, datasets, named rule sets on each dataset, and settings.
- `diffra record`: one command per job that produces data, built from the existing operations.
  Invalid input is reported, not fatal. Built for `--value`, `--skipped` and `--failed`, and reads
  `from` for `diffra/size@1` with the `files` converter and for `diffra/findings@1` with the
  `sarif` converter; the other types wait for their converters. `diffra merge` merges every job's entries in the collecting
  job; the partial record it writes and the transport between jobs come with the Action.
- Built-in converters for every standard dataset type before the first installation, each tested
  from the tool's validator and synthetic values.
- CI timing and the job graph, with wait, queue and execution time recorded. Rules apply to
  execution time only.

### 4. Comparison and history

- Compare with the record at the target branch tip when CI tests the merged result, otherwise at
  the merge base. Fall back to the nearest recorded ancestor inside the history window and state
  the distance. Say "cannot attribute" when nothing in the window has a record.
- The history window as a setting, read and written through a pluggable storage strategy, with
  a branch in the repository as the default store. Records carry a format version; unreadable
  ones are skipped and counted.
- Noise: fixed thresholds for deterministic measures; the history window for noisy ones.
- Remove the Baseline reference document, approval and promotion. Records are named by commit,
  project and dataset; the hash becomes an integrity check and storage key, computed only by the
  CLI, with the environment outside it.

### 5. The GitHub surface

- The Action's metadata at the root of this repository; the collecting job for each workflow
  file.
- One comment per pull request, rebuilt from every record; nothing on a quiet pull request; the
  summary page on read-only runs; one checks-list line and the `diffra-result` artifact on every
  run.
- Two builds: a CI build without the web server, and a local build with it.

### 6. Diffra on itself

- Install Diffra in this repository the adopter's way and retire the self-evaluation scripts,
  keeping only what the separate, non-gating lane needs.
- Benchmark the converters on real input.
- Fixture repositories that test the comparison point under five branching models.

### 7. Adopt

Adopt the three repositories in order, applying the review triggers to each.

## Rules for each change

- Zero warnings. Every change has tests.
- The frozen regression corpus under `tests/regression/` is not edited. A change that breaks it is
  a release decision for the owner.
- Keep record formats, schemas and comparison generic, tied to no adopter or CI vendor.
- Update the feature status table in [docs/README.md](docs/README.md), the matching decision
  record and [docs/vision.md](docs/vision.md) when a change lands.
