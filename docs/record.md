# Recording a run

This page specifies `diffra record`, the one command each job that produces data runs, and the
entries file it writes. The command is built for `--value`, `--skipped` and `--failed`. No
converter exists yet, so it cannot read the files a dataset's `from` names: `--from`, or giving no
source, is saved as a rejected entry that says so. The protocol library's `Recording.cs` validates
entries, writes and reads entries files, and merges them by the rules below; no collecting job
runs the merge yet.

## What is decided

These points are decided in the decision records; everything else on this page is *proposed*.

- Each job that produces data runs one command, `diffra record`
  ([0002](adr/0002-project-file-and-datasets.md) decision 8).
- `record` reads the files the dataset's `from` names in the root file, with the converter it
  names, or takes a single number with `--value`. `from` is the preferred form. There is no
  payload on standard input.
- `record` validates every value against the root file at once. An invalid value is saved as a
  rejected entry and `record` still exits 0, so the final step reports every problem together. A
  final step checks that every required dataset was collected.
- What a job hands on holds typed values with dimensions, the job's identity, and skipped, failed
  and rejected entries. Raw source files are included only on request
  ([0004](adr/0004-records-integrity-and-storage.md) decision 6).

## The command (proposed)

```sh
diffra record --name <dataset> [source] [--project <name>] [--key <key>]
  [--commit <id>] [--job <name>] [--attempt <n>] [--dimension <name>=<value>]...
  [--replace] [--root-file <file>] [--run-dir <dir>] [--json]
```

`--name` names a dataset declared in the root file. At most one source is given:

| Source | Use |
| --- | --- |
| *(none)* | Read the files the dataset's `from` names, with the converter it names. |
| `--from <path-or-glob>` | Read these files instead of the paths in `from`, still with the converter `from` names. |
| `--value <number> [--unit <unit>]` | One number. Numbers are typed normally; the CLI writes the canonical form. Without `--unit`, the dataset's declared unit applies and none is written; giving the declared unit is the same as leaving it out. |
| `--skipped --reason <text>` | The dataset was deliberately not collected in this job. |
| `--failed --reason <text>` | The collector broke. |

`--value` takes a number only. The root file schema requires `from` on every dataset, so how a
dataset that is only ever given with `--value` is declared is open. `--key` names the member of a
keyed dataset that a `--value` belongs to.

When the converter cannot run, the entry is `rejected`, with the sources it did read: `from` or
`--from` matches no file, a file does not parse, a file lies outside the repository, or more files
match than the schema's safety limit. Giving two sources at once is also rejected.

### Context

| Value | Taken from |
| --- | --- |
| Project | `--project`, then `DIFFRA_PROJECT`. |
| Commit | `--commit`, then `DIFFRA_COMMIT`. A full commit ID only. |
| Job | `--job`, then `DIFFRA_JOB`; attempt from `--attempt`, then `DIFFRA_JOB_ATTEMPT`, then 1. |
| Dimensions | `--dimension <name>=<value>`, repeatable, then `DIFFRA_DIMENSION_<NAME>`. |
| Run folder | `--run-dir`, then `DIFFRA_RUN_DIR`, then `diffra/run` under the repository's Git directory, which is never tracked. |
| Root file | `--root-file`, then `DIFFRA_ROOT_FILE`. The root file's name in a repository is not decided, so there is no default; without one the entry is rejected. |

An environment variable that is set but empty counts as not set, as does an empty `--run-dir`.

The CLI does not ask the CI host for any of these, and it does not read the commit from the
checkout: a pull request build often checks out a merge commit that CI made, not the commit being
recorded. A CI integration, such as Diffra's Action, sets the environment variables; the CLI
itself stays independent of the CI vendor. Mapping a dimension name to an upper-case variable name
loses case, so two dimension names that differ only in case cannot both be set this way. A
dimension variable is read only for the dimensions the dataset is stratified by.

### Behaviour

- `record` checks that the project and dataset are declared and the dataset is selected by the
  project, its type, its unit, the value's encoding and each dimension.
- It appends one entry to the job's entries file and does nothing else: no comparison, no network
  access and no write to history.
- The entry holds the typed values the converter read and the path, converter and SHA-256 digest
  of every file read. How raw files are requested is not yet specified.
- Recording the same value twice is accepted. A different value for the same project, dataset,
  key and dimensions in the same job and attempt is rejected unless `--replace` is given.
  `--replace` applies to recorded entries only.

### Output and exit codes

One line for a person, or one JSON object with `--json` for an agent.

A rejected entry keeps what was given, by option name, and the problems found. `record` exits
non-zero only when it cannot save an entry, because the problem would otherwise be lost: 1 when no
run folder can be found or the entries file cannot be written, and 2 when there is no usable
dataset name, project, commit or job to write the entry under, or `--run-dir` is given twice. A commit that is not a full ID, an attempt that is not a positive whole
number, and a job name with surrounding whitespace or control characters are unusable and exit 2.

## The entries file (proposed)

Each job writes one append-only file of JSON lines in `<run-dir>/entries/`, the first form of
what a job hands on. Each line is one entry that validates against
[`schemas/record-entry-v1.schema.json`](../schemas/record-entry-v1.schema.json). Examples are in
[`examples/record/`](../examples/record/). The file name is the job name and attempt, made safe
for a file name, plus a short digest of both so that two names that sanitise alike do not
collide. The name carries no meaning; the entries say which job and attempt wrote them. The
environment the job ran in is recorded with what the job hands on, not in each entry.

| Field | Meaning |
| --- | --- |
| `project`, `dataset`, `commit` | What was recorded and for which commit. |
| `job` | The job's `name` and its `attempt`, which is 1 unless the job was re-run. |
| `dimensions` | The value of each dimension for this job, such as `{"os": "linux"}`. Omitted when there are none. |
| `key` | The member of a keyed dataset, with a `value` only. |
| `status` | `recorded`, `skipped`, `failed` or `rejected`. |
| `value` | A `--value` in canonical form, with its `unit` when one was given. |
| `sources`, `data` | Every file read (path, converter, digest) and the typed values read from them, in the shape of the dataset's type. |
| `reason` | Why a dataset was skipped or failed. |
| `input`, `problems` | For a rejected entry: every option given, as raw text by option name (a list when an option was repeated), and why it was rejected. A rejected entry's `project`, `dataset` and `dimensions` are kept as given, even when the root file does not declare them. |
| `replaces` | `true` when `--replace` was given. |

A canonical value is a decimal string with no exponent, no plus sign, no leading zeros, no trailing
zeros after the decimal point and no negative zero: `12.5`, not `12.50`, `+12.5` or `1.25e1`; `0`,
not `-0`. A source path is relative to the repository root, uses single forward slashes, and has
no `.` or `..` segment, no leading `/` or `~`, no trailing `/`, no backslash and no control
characters. The schema's length and count limits are safety limits: an entry that would exceed
them is rejected, as is an entry nested more than 256 deep or holding text that is not valid
Unicode, and raw text kept in a rejected entry is cut to the limit, with the cut stated
in `problems`.

## Merging the jobs' entries (proposed)

[0004](adr/0004-records-integrity-and-storage.md) lists the merge rules as not yet decided. The
collecting job of a workflow file ([0004](adr/0004-records-integrity-and-storage.md) decision 7)
gathers every job's entries file and takes their union. An entry's identity is its project,
dataset, key and dimensions; every entry in a merge must name the same commit, and an entry for
another commit is a finding.

1. A later attempt of the same job replaces every entry of the earlier attempt. The merge notes
   which attempts were superseded.
2. Within one job and attempt, an entry with `replaces` replaces the earlier recorded entry with
   the same identity.
3. Entries with the same identity and status and an equal canonical `value` or equal `data` are
   identical and are kept once, whether they came from one job or several; the first job in name
   order is kept.
4. Two different values with the same identity are a finding; neither value is chosen. Within one
   job and attempt this arises only from a hand-edited file, since `record` refuses it; the
   identity is still compared across jobs, so a disagreement between other jobs is reported too.
   Two more cases are findings for the whole dataset and dimensions, whatever the key, and keep
   none of its entries: entries with different statuses (such as `recorded` against `skipped`, or
   `skipped` against `failed`), whether from one job or several, and a `--value` entry against data
   read from files. These are checked over every entry of each job's last attempt, so a conflict
   within a job does not hide them. Each finding names every job involved.
5. A required dataset with no entry for a dimension value the root file plans for is reported as
   not collected. This is found from the root file, not from the CI host, so a job that died before
   uploading anything is still noticed.

The merged entries become the partial record for the commit that the workflow file writes. They
travel in the same bundle format as every other stage
([0004](adr/0004-records-integrity-and-storage.md) decision 6), with its import checks.
