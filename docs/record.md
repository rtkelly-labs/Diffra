# Recording a run

This page specifies `diffra record`, the one command each job that produces data runs, and the
entries file it writes. The command is built for `--value`, `--skipped` and `--failed`. No
converter exists yet, so it cannot read the files a dataset's `from` names: `--from`, or giving no
source, is saved as a rejected entry that says so. The protocol library's `Recording.cs` validates
entries, writes and reads entries files, and merges them by the rules below, and `diffra merge`
runs that merge for a collecting job.

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
| `--value <number> [--unit <unit>]` | One number. Numbers are typed normally; the CLI writes the canonical form. Without `--unit`, the dataset's declared unit applies and none is written; giving the declared unit is the same as leaving it out. A `bytes` dataset is measured in `B`; a `duration` takes one of `ns`, `us`, `ms`, `s`, `m`, `h` or `d`; a `ratio` takes no unit. |
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

## The collecting step

Each workflow file has one collecting job. It runs one command, `diffra merge`, after the jobs
that produce data, whether they succeeded or not.

```sh
diffra merge [--commit <id>] [--root-file <file>] [--run-dir <dir>] [--entries <dir>]...
  [-o <file>] [--fail-on never|fail|warn] [--json]
```

- `--entries` names a folder holding the jobs' entries files, and may be repeated; a folder named
  twice, with or without a trailing separator, is read once. Without it, `merge` reads
  `<run-dir>/entries`, which may not exist when no job recorded anything. The run folder, the
  commit and the root file are found as for `record`. An option given with an empty value is an
  error.
- Every file whose name ends in `.jsonl` directly in each folder is read, in name order. Entries
  files come from other jobs and are read as untrusted data: links are not followed and folders
  are not searched. Only the first 1,000 such files in all are read, and how many more there were
  is reported. A file over 16 MiB, or one that would take the total past 256 MiB, is reported and
  not read. A file with the same bytes as one already read is reported and not read again. A file
  that cannot be read is reported, and the merge goes on without it.
- A file whose entries are the first entries of another file, in the same order, is an earlier
  copy of that file, such as an artifact uploaded twice. It is reported and not merged. Every other
  file is merged, even two that hold the same job and attempt, so that where they disagree the
  merge reports it as a finding.
- Each line that is not a valid entry is reported and left out, up to 20 lines for each file and
  then one line saying how many more there were. Reading a file stops after 100 such lines. After
  1,000 problems in all, one line says that there were more.
- `merge` checks each entry against the entry schema and the rules above, not against the root
  file. `record` checked the entries it saved against the root file in force when it ran, so an
  entry for a dataset the root file no longer declares is merged as it is.
- The plan for rule 5 is every required dataset of every project, once for each combination of
  the listed values of the dimensions it is stratified by. An observed dimension lists no values,
  so the plan leaves it out, and an entry with any value for it matches.
- The merged entries are written to `-o`, or `<run-dir>/merged.jsonl`, as an entries file: one
  valid entry per line, in the merge's order. The same entries give the same file. The output
  must be a file path, and may be neither the root file nor in an entries folder the merge reads,
  with links followed.
- `merge` prints one line for the result and one for each superseded attempt, finding and
  problem, listing at most 1,000 superseded attempts and 1,000 findings and then saying how many
  more there were. Text that comes from the entries files, file names or the command line is
  printed with control characters, the Unicode line and paragraph separators and the
  bidirectional controls written as `\uXXXX`, so that no line can pass for another.
- It exits 0, or 3 when what it finds fails the step under `fail-on` (see "Failing the step"
  below); 1 when it cannot read the entries folders or write its output; and 2 when there is no
  usable commit, root file, run folder, entries folder or output path, or an option is wrong.

With `--json`, `merge` prints one JSON object instead:

| Field | Meaning |
| --- | --- |
| `commit` | The commit merged for. |
| `entriesFile` | The full path of the merged entries file. |
| `entries` | How many entries it holds. |
| `entriesFiles` | How many entries files were read. |
| `failOn` | The `fail-on` setting in force: `never`, `fail` or `warn`. |
| `failsStep` | Whether the findings fail the step under that setting. |
| `supersededAttempts` | Each superseded attempt, as `<job> attempt <n>`, up to 1,000. |
| `supersededAttemptsNotListed` | How many more superseded attempts there were. |
| `findings` | Each finding, as `{ "kind", "message" }`, up to 1,000. A finding that counts towards `fail-on` also has `countsAs`, the status it counts as. |
| `findingsNotListed` | How many more findings there were. |
| `unreadable` | Each problem met while reading, as `{ "file", "message" }`. The `file` is empty for the line that says more problems were found. |

A finding's `kind` is one of:

| Kind | Meaning |
| --- | --- |
| `other-commit` | An entry names another commit; it is left out. |
| `conflict` | Entries for one dataset, key and dimensions disagree; no value is chosen. |
| `status-conflict` | Entries for one dataset and dimensions have different statuses. |
| `value-and-data` | A dataset was given with `--value` in one job and read from files in another. |
| `not-collected` | The root file plans for a dataset and dimensions that no job recorded. |
| `collection-failed` | The root file plans for a required dataset and dimensions that jobs recorded only as `failed`. |
| `rejected` | A job gave something `record` could not record. The rejected entry is kept in the merged file, and the finding names the job and shows the first three of its problems. |

Failing the step:

`merge` exits 0 whatever it finds, unless the adopter opts in with `--fail-on`, or the
`DIFFRA_FAIL_ON` variable, set to `never`, `fail` or `warn` (see
[decision 0005](adr/0005-surface-do-not-enforce.md)). A required dataset that no job collected,
including one collected only as rejected entries, is a `not-collected` finding and counts as a
warning: it fails the step under `warn` and not under `fail`. A required dataset whose entries
for a planned dimension value are all `failed` is a `collection-failed` finding and counts as
`fail`, because the collector broke: it fails the step under `fail` and `warn`. A `skipped` entry
was left out on purpose and counts as collected. When the findings fail the step,
`merge` still writes the merged file and the report, says so on standard error, and exits 3. No
other finding counts towards `fail-on` yet; see "Not yet decided" below. Any other value of the
setting is an error, so a misspelling never turns enforcement off.

Decided and not built yet:

- Datasets produced by another workflow file. Each workflow file has its own collecting job; the
  root file will say which workflow file is responsible for which datasets, and a collecting job
  will not report a dataset another workflow file is responsible for as not collected. Until the
  root file can say so, a collecting job reports a required dataset produced elsewhere as not
  collected.

Not yet decided:

- The format of the partial record, and how it travels and is written to history.
- Whether a conflict, a status conflict, a dataset given two ways, an entry for another commit or
  a rejected entry for an optional dataset counts towards `fail-on`. Until it is decided, none of
  them does.
- How jobs' entries files reach the collecting job. A CI integration owns that transport; on
  GitHub, each job uploads its file as an artifact and the collecting job downloads them into one
  folder.
