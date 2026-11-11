# Recording a run

This page specifies `diffra record`, the one command each job that produces data runs
([0002](adr/0002-project-file-and-datasets.md) decision 8), and the entries file it writes. It is
a specification: the command does not exist yet. Parts marked *proposed* are not yet ruled.

## The command

```sh
diffra record --name <dataset> [source] [--project <name>] [--key <key>] \
  [--replace] [--run-dir <dir>] [--json]
```

`--name` names a dataset declared in the root file. Exactly one source is given:

| Source | Use |
| --- | --- |
| *(none)* | Read the files the dataset's `from` names in the root file, with the converter it names. This is the preferred form. |
| `--from <path-or-glob>` | Read these files instead of the paths in `from`, still with the converter `from` names. |
| `--value <number> [--unit <unit>]` | One number, for a small scalar dataset. Numbers are typed normally; the CLI writes the canonical form. |
| `--skipped --reason <text>` | The dataset was deliberately not collected in this job. |
| `--failed --reason <text>` | The collector broke. |

`--key` names the member of a keyed dataset that a `--value` belongs to, and can be repeated across
calls. Reading from a payload on standard input was proposed and is not part of the command.

### Context

| Value | Taken from |
| --- | --- |
| Project | `--project`, then `DIFFRA_PROJECT`. |
| Commit | `--commit`, then `DIFFRA_COMMIT`, then `git rev-parse HEAD` in the working directory. A full commit ID only. |
| Job | `--job`, then `DIFFRA_JOB`; attempt from `--attempt`, then `DIFFRA_JOB_ATTEMPT`. |
| Dimensions | `--dimension <name>=<value>`, repeatable, then `DIFFRA_DIMENSION_<NAME>`. |
| Run folder | `--run-dir`, then `DIFFRA_RUN_DIR`, then *(proposed)* `diffra/run` under the repository's Git directory, which is never tracked. |

Option and variable names other than `--project`, `DIFFRA_PROJECT`, `--run-dir` and
`DIFFRA_RUN_DIR` are proposed. The CLI does not ask the CI host for any of these. A CI integration, such as Diffra's Action, sets
the environment variables; the CLI itself stays independent of the CI vendor.

### Behaviour

- `record` validates at once against the root file: that the dataset is declared and selected by
  the project, its type, its unit, the value's encoding and each dimension.
- It appends one entry to the job's entries file and does nothing else: no comparison, no network
  access and no write to history.
- Recording the same value twice is accepted. A different value for the same dataset, key and
  dimensions in the same job is rejected unless `--replace` is given.
- Raw source files are not kept. The entry holds the typed values the converter read and the
  SHA-256 digest of each file read.

### Output and exit codes

One line for a person, or one JSON object with `--json` for an agent.

An invalid value does not fail the step. It is saved as a `rejected` entry and `record` exits 0,
so the final step reports every problem together. *Proposed:* `record` exits non-zero only when it
cannot save the entry, because the problem would otherwise be lost: 1 when the entries file cannot
be written, 2 when the command line names no dataset at all.

## The entries file

Each job writes one append-only file of JSON lines, `<run-dir>/entries/<job>.jsonl`, where `<job>`
is the job name made safe for a file name. Each line is one entry that validates against
[`schemas/record-entry-v1.schema.json`](../schemas/record-entry-v1.schema.json). Examples are in
[`examples/record/`](../examples/record/).

| Field | Meaning |
| --- | --- |
| `project`, `dataset`, `commit` | What was recorded and for which commit. |
| `job` | The job's `name` and, when re-run, its `attempt`. |
| `dimensions` | The value of each dimension for this job, such as `{"os": "linux"}`. |
| `key` | The member of a keyed dataset. |
| `status` | `recorded`, `skipped`, `failed` or `rejected`. |
| `value` | A `--value` in canonical form, with its `unit`. |
| `source`, `data` | The file read (relative path, converter, digest) and the typed values read from it. |
| `reason` | Why a dataset was skipped or failed. |
| `problems` | Why an entry was rejected. A rejected entry keeps the `value` or `source` it was given. |
| `replaces` | `true` when `--replace` was given. |

A canonical value is a decimal string with no exponent, no leading plus sign, no leading zeros and
no trailing zeros after the decimal point: `12.5`, not `12.50` or `1.25e1`.

## Merging the jobs' entries

The collecting job gathers every job's entries file
([artifact delivery](artifact-delivery-and-ci.md)) and takes their union:

1. A later attempt of the same job replaces every entry of the earlier attempt.
2. Within one job, an entry with `replaces` replaces the earlier entry for the same dataset, key and
   dimensions.
3. Identical entries from different jobs are kept once.
4. Two different values for the same dataset, key and dimensions from different jobs are a
   finding; neither value is chosen.
5. A required dataset with no entry for a dimension value the root file plans for is reported as
   not collected. This is found from the root file, not from the CI host, so a job that died before
   uploading anything is still noticed.

The merged entries become the partial record for the commit that this workflow file writes.
