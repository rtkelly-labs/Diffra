# Recording a run

This page specifies `diffra record`, the one command each job that produces data runs
([0002](adr/0002-project-file-and-datasets.md) decision 8), and the entries file it writes. It is
a specification: the command does not exist yet. Parts marked *proposed* are not yet ruled.

## The command

```sh
diffra record --name <dataset> [source] [--project <name>] [--key <key>]
  [--commit <id>] [--job <name>] [--attempt <n>] [--dimension <name>=<value>]...
  [--replace] [--run-dir <dir>] [--json]
```

`--name` names a dataset declared in the root file. At most one source is given:

| Source | Use |
| --- | --- |
| *(none)* | Read the files the dataset's `from` names in the root file, with the converter it names. This is the preferred form. |
| `--from <path-or-glob>` | Read these files instead of the paths in `from`, still with the converter `from` names. |
| `--value <number> [--unit <unit>]` | One number, for a small scalar dataset. Numbers are typed normally; the CLI writes the canonical form. |
| `--skipped --reason <text>` | The dataset was deliberately not collected in this job. |
| `--failed --reason <text>` | The collector broke. |

`--value` takes a number only. The root file schema still requires `from` on every dataset, so how a
dataset that is only ever given with `--value` is declared is open.

`--key` names the member of a keyed dataset that a `--value` belongs to, and can be repeated across
calls. Reading from a payload on standard input was proposed and is not part of the command.

### Context

| Value | Taken from |
| --- | --- |
| Project | `--project`, then `DIFFRA_PROJECT`. |
| Commit | `--commit`, then `DIFFRA_COMMIT`. A full commit ID only. |
| Job | `--job`, then `DIFFRA_JOB`; attempt from `--attempt`, then `DIFFRA_JOB_ATTEMPT`, then 1. |
| Dimensions | `--dimension <name>=<value>`, repeatable, then `DIFFRA_DIMENSION_<NAME>`. |
| Run folder | `--run-dir`, then `DIFFRA_RUN_DIR`, then *(proposed)* `diffra/run` under the repository's Git directory, which is never tracked. |

Option and variable names other than `--project`, `DIFFRA_PROJECT`, `--run-dir` and
`DIFFRA_RUN_DIR` are proposed. The CLI does not ask the CI host for any of these, and it does not
read the commit from the checkout: a pull request build often checks out a merge commit that CI
made, not the commit being recorded. A CI integration, such as Diffra's Action, sets the
environment variables; the CLI itself stays independent of the CI vendor.

### Behaviour

- `record` validates at once against the root file: that the project and dataset are declared and
  the dataset is selected by the project, its type, its unit, the value's encoding and each
  dimension.
- It appends one entry to the job's entries file and does nothing else: no comparison, no network
  access and no write to history.
- Raw source files are not kept by default. The entry holds the typed values the converter read
  and the path, converter and SHA-256 digest of every file read. A bundle includes raw files only
  on request ([0004](adr/0004-records-integrity-and-storage.md) decision 6); how that request is
  made is not yet specified.
- *Proposed:* recording the same value twice is accepted. A different value for the same project,
  dataset, key and dimensions in the same job and attempt is rejected unless `--replace` is given.
  `--replace` applies to recorded entries only.

### Output and exit codes

One line for a person, or one JSON object with `--json` for an agent.

An invalid value does not fail the step. It is saved as a `rejected` entry that keeps what was
given, and `record` exits 0, so the final step reports every problem together. *Proposed:*
`record` exits non-zero only when it cannot save the entry, because the problem would otherwise be
lost: 1 when the entries file cannot be written, 2 when there is no dataset name, project, commit
or job to write the entry under.

## The entries file

Each job writes one append-only file of JSON lines in `<run-dir>/entries/`. Each line is one entry
that validates against
[`schemas/record-entry-v1.schema.json`](../schemas/record-entry-v1.schema.json). Examples are in
[`examples/record/`](../examples/record/). *Proposed:* the file name is the job name and attempt,
made safe for a file name, plus a short digest of both so that two names that sanitise alike do not
collide. The name carries no meaning; the entries say which job and attempt wrote them.

| Field | Meaning |
| --- | --- |
| `project`, `dataset`, `commit` | What was recorded and for which commit. |
| `job` | The job's `name` and its `attempt`, which is 1 unless the job was re-run. |
| `dimensions` | The value of each dimension for this job, such as `{"os": "linux"}`. Omitted when there are none. |
| `key` | The member of a keyed dataset, with a `value` only. |
| `status` | `recorded`, `skipped`, `failed` or `rejected`. |
| `value` | A `--value` in canonical form, with its `unit`. |
| `sources`, `data` | Every file read (relative path, converter, digest) and the typed values read from them. |
| `reason` | Why a dataset was skipped or failed. |
| `input`, `problems` | For a rejected entry: what was given, as raw text by option name, and why it was rejected. A rejected entry's `project` and `dataset` are kept as given, even when the root file does not declare them. |
| `replaces` | `true` when `--replace` was given. |

A canonical value is a decimal string with no exponent, no plus sign, no leading zeros, no trailing
zeros after the decimal point and no negative zero: `12.5`, not `12.50`, `+12.5` or `1.25e1`; `0`,
not `-0`. A source path is relative to the working directory, uses single forward slashes, and has
no `.` or `..` segment, no leading `/` or `~` and no control characters.

## Merging the jobs' entries

*Proposed*: [0004](adr/0004-records-integrity-and-storage.md) lists the merge rules as not yet
decided. The collecting job of a workflow file ([0004](adr/0004-records-integrity-and-storage.md)
decision 7) gathers every job's entries file and takes their union. An entry's identity is its
project, dataset, key and dimensions.

1. A later attempt of the same job replaces every entry of the earlier attempt. The merge notes
   which attempts were superseded.
2. Within one job and attempt, an entry with `replaces` replaces the earlier recorded entry with
   the same identity.
3. Entries with the same identity and status and an equal canonical `value` or equal `data` are
   identical; identical entries from different jobs are kept once.
4. Two different values with the same identity from different jobs are a finding; neither value is
   chosen. So is a `recorded` entry from one job against a `skipped` or `failed` entry from another.
5. A required dataset with no entry for a dimension value the root file plans for is reported as
   not collected. This is found from the root file, not from the CI host, so a job that died before
   uploading anything is still noticed.

The merged entries become the partial record for the commit that the workflow file writes. They
travel in the same bundle format as every other stage
([0004](adr/0004-records-integrity-and-storage.md) decision 6).
