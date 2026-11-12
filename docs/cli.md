# CLI reference

The `diffra` CLI has eight commands. `record` appends one entry to its job's entries file; each
of the others reads files, validates them, and writes one output atomically. `collect`, `diff`, `eval` and `report` refuse an output path that names one of their
inputs. `bundle export` replaces its output file, and `present bundle` replaces its output
directory, so keep those outputs apart from their inputs. This page describes the commands and the
two hand-written input files, the metrics payload and the rules file. The document shapes the
commands write are in the [protocol](protocol.md).

Run the CLI from a build with `dotnet run --project src/Cli/Diffra.Cli.csproj -c Release --no-build -- <command>`.
`diffra --help` prints the usage line for every command; `record`, `merge`, `collect`, `bundle` and
`present` also accept `--help`.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | The command completed. For `eval`, the Assessment outcome is `pass` or `warn`. |
| 1 | A file or execution error, such as an unreadable path. |
| 2 | Invalid input: an unknown or repeated option, a malformed document, or a digest mismatch. |
| 3 | `eval`: the Assessment outcome is `fail`, `needs-review` or `inconclusive`. `merge`: the findings fail the step under `fail-on`. The output is still written. |

`record` is the exception: it saves invalid input as a rejected entry and exits 0, and exits 1 or
2 only when it cannot save an entry. `merge` exits 0 whatever the merge finds, unless the adopter sets `fail-on`. See below.

`diff` does not use the exit code to report change. A changed and an unchanged comparison both exit
0; the Delta's `status` records which.

## `record`

Records one dataset for the job it runs in, by appending one entry to the job's entries file.
[Recording a run](record.md) specifies the command, the entries file and how the collecting job
merges the entries of every job.

```sh
diffra record --name <dataset> [--value <number> [--unit <unit>] [--key <key>] [--replace]
  | --from <path-or-glob> | --skipped --reason <text> | --failed --reason <text>]
  [--project <name>] [--commit <id>] [--job <name>] [--attempt <n>] [--dimension <name>=<value>]... [--root-file <file>]
  [--run-dir <dir>] [--json]
```

No converter is built yet, so `record` cannot read the files a dataset's `from` names: a number
is given with `--value`, and `--from`, or giving no source, is saved as a rejected entry. `record`
reads the root file from `--root-file`, then `DIFFRA_ROOT_FILE`, and checks the value against it.
Every problem is saved in a rejected entry and `record` exits 0. It exits 2 when there is no
usable dataset name, project, commit or job, or a repeated `--run-dir`, and 1 when no run folder
can be found or the entries file cannot be written.

## `merge`

Merges the entries files of every job for one commit, in the collecting job. It writes the merged
entries as an entries file and reports conflicts, superseded attempts, datasets the root file
plans for that were not collected, and lines or files it could not read.

```sh
diffra merge [--commit <id>] [--root-file <file>] [--run-dir <dir>] [--entries <dir>]...
  [-o <file>] [--fail-on never|fail|warn] [--json]
```

What the merge finds fails the command only when the adopter opts in with `--fail-on`, or
`DIFFRA_FAIL_ON`: under `warn`, a required dataset that was not collected exits 3 after the output
is written. It exits 1 when it cannot read the entries folders
or write its output, and 2 when there is no usable commit, root file, run folder, entries folder
or output path, or an option is wrong. [Recording a run](record.md#the-collecting-step) gives the
rules, the limits and the `--json` shape.

## `collect`

Turns a metrics payload into an Evidence document.

```sh
diffra collect --payload <json-file> --subject-id <id> --commit <full-commit> \
  --type-id <id> --type-version <version> \
  --producer-id <id> --producer-version <version> -o <evidence-file>
```

| Option | Rule |
| --- | --- |
| `--payload` | The metrics payload described below. |
| `--subject-id` | A stable name for what was observed, such as `example/project`. |
| `--commit` | A full commit ID: 40 or 64 lowercase hexadecimal characters. A branch name is rejected. |
| `--type-id` | Lowercase reverse-DNS, at least two dot-separated parts, such as `org.example.metrics`. It names what the evidence means, not its file format. |
| `--type-version` | `major.minor` or `major.minor.patch`. |
| `--producer-id`, `--producer-version` | The tool that made the observation. |
| `-o` | The Evidence file to write. |

`collect` records the platform, the .NET runtime and the SDK version in the Evidence's
`environment`. It writes no artifact attachments.

### The metrics payload

```json
{
  "scope": "example/project",
  "methodology": "sample-metrics-v1",
  "metrics": {
    "complexity": { "value": "5", "unit": "cyclomatic-complexity" },
    "methodCount": { "value": "2", "unit": "methods" }
  }
}
```

- `scope` and `methodology` are required strings, and the only other permitted key is `metrics`.
  Two Evidence documents are compared only when both match exactly; a change of methodology makes
  the comparison `incompatible` rather than a measured change.
- `metrics` is an object. Each member is one of three things: a bare JSON number (a measurement
  with no unit); an object with a `value` and optional `unit` and nothing else (a measurement); or
  any other object (a group, whose members are read the same way). Anything else is rejected. Each
  measurement is addressed by its JSON Pointer path, such as `/complexity` or `/build/warnings`.
- `value` is a finite decimal. Integers may be JSON numbers; anything with a fractional part must be
  a string such as `"12.5"`, because Diffra's canonical JSON accepts only integer number tokens.
- `unit` is an optional non-empty string. A measurement whose unit changes between baseline and
  candidate makes the comparison `incompatible`.

The payload is bounded in size; an oversized file is rejected before parsing.

## `diff`

Compares candidate Evidence with baseline Evidence and writes a policy-free Delta.

```sh
diffra diff --baseline-ref <reference.json> --baseline-evidence <evidence.json> \
  --evidence <candidate.json> -o <delta.json>
```

`diff` verifies every input's identity and digests, checks that the Baseline reference names the
supplied baseline Evidence and its commit, and then compares measurements by path:

| Situation | Change recorded |
| --- | --- |
| Value differs | `modified`, with `before`, `after`, `absolute` (after minus before) and `relative` (absolute divided by before, as a fraction: `0.25` is 25%) |
| Baseline value is zero | `modified`, with `relative` null and `relativeStatus: "undefined-zero-baseline"` |
| Path only in the candidate | `added` |
| Path only in the baseline | `removed` |

`diff` writes `before`, `after`, `absolute` and `relative` in one canonical form: a plain
invariant decimal string with an optional leading `-`, digits, and an optional fraction with no
trailing zeros. It never uses exponent notation, so a one-byte change on a 184125-byte package is
written as `"0.0000054310930074677528852682"`, not `"5.43...E-06"`. `eval` rejects decimal
strings in exponent notation.

`diff` checks the Baseline reference's structure and identity but **does not authenticate its
issuer**. Do not gate a build on a reference supplied by the pull request under test; see
[trust and identity](trust-and-identity.md).

## `eval`

Applies a rules file to a Delta and writes an Assessment.

```sh
diffra eval --rules <rules.json> --delta <delta.json> -o <assessment.json>
```

### The rules file

```json
{
  "id": "org.example.complexity-budget",
  "version": "1.0.0",
  "warnAboveAbsoluteIncrease": "0",
  "failAboveAbsoluteIncrease": "3",
  "warnAboveRelativeIncrease": "0.10",
  "failAboveRelativeIncrease": "0.50"
}
```

`id` and `version` are required and name the policy in the Assessment. At least one threshold is
required, and none may be negative. The rules are budgets on **increases**:

- A decrease, or no change, passes.
- An increase strictly above a `fail` threshold fails; otherwise, strictly above a `warn` threshold
  warns. Absolute thresholds are in the measurement's unit; relative thresholds are fractions.
- If a relative threshold is set and the baseline value was zero, the relative change is undefined
  and the finding is `needs-review`.
- An `added` or `removed` measurement is always `needs-review`. A new metric is not zero.

The Assessment's outcome is the worst finding, in the order `fail`, `inconclusive`,
`needs-review`, `warn`, `pass`. A Delta that is not `changed` or `unchanged` cannot pass.

## `report`

Renders a Delta, and optionally its Assessment and Baseline reference, for a person to read.

```sh
diffra report <delta.json> [--assessment <file>] [--baseline-ref <file>] \
  [--format html|json|markdown|comment|agent] [-o <file>]
```

The default format is HTML; without `-o` the report goes to standard output. A report without an
Assessment says it has no policy verdict. Evidence-derived text is escaped, and the HTML loads no
scripts or remote assets.

`comment` and `agent` are the one-screen formats
([0006](adr/0006-pr-output-and-attention-levels.md)). Each finding gets an attention level: a
failed rule needs action (`act`); a warning, a value that needs review or an inconclusive
comparison is worth reviewing (`review`); a pass is `quiet`. An Assessment outcome that no
finding explains still sets the level. Without an Assessment, every change is `review`. Findings are ranked by level, then rule severity, then the size of the relative
change.

- `comment` is the pull request comment: a heading with the level, then a table of at most 5
  findings with before, after and change, within 25 lines. Further findings become one overflow
  line, with the full list in a collapsed section. A quiet result writes an empty file, because
  nothing is posted. The command writes the comment; posting it is a separate step.
- `agent` is plain Markdown for an agent: `attention:` with `act`, `review` or `quiet`,
  `findings:` with the number of findings that are not quiet, then one line per finding while the file stays within about 500
  tokens. A `truncated:` line says how many findings were left out.

## `bundle`

Moves evidence between machines through a portable archive and a local content-addressed store.

```sh
diffra bundle export --manifest <file> -o <bundle.zip> [--store <cas-dir>] [--artifacts-dir <dir>] \
  [--delta <file>] [--assessment <file>] [--baseline-ref <file>]
diffra bundle import --bundle <bundle.zip> --store <cas-dir>
```

`export` writes the archive to a temporary file and then replaces the `-o` file if one exists; it
does not check whether that path is one of its inputs.

Import checks each artifact's bytes against the digest the manifest declares for it, or against its
content address, checks that every artifact the manifest declares is present, and rejects entries
that would escape the target directory. It stores the manifest and the Delta, Assessment and
Baseline reference documents as given, without verifying their embedded IDs; `diff` and `report`
verify document identity when they read them. The manifest and directory layout are described in
[artifact delivery and CI](artifact-delivery-and-ci.md).

## `present`

Builds a self-contained review bundle and serves it locally.

```sh
diffra present bundle --delta <delta.json> -o <output-dir> [--assessment <file>] [--baseline-ref <file>] \
  [--manifest <file>] [--artifacts-dir <dir>] [--store <cas-path>]
diffra present serve <bundle-directory> [--port <port>]
```

`present bundle` builds the bundle in a temporary directory and then swaps it in at `-o`. The output
directory must be empty, absent, or a previous presentation output (one containing `inventory.json`
and `index.html`); a previous output is replaced, so do not keep inputs inside it.

`serve` listens on `127.0.0.1` only (port 5055 by default), rejects requests for any other host,
and sends a strict Content-Security-Policy.

## Self-verification

Diffra runs this CLI on itself in CI. `dotnet run scripts/RunDogfood.cs` collects metrics about the
repository, resolves the comparison point Evidence document from the `diffra-baselines` branch,
diffs candidate metrics against that baseline with the pinned judge (`Diffra.Cli`), evaluates them
under `baselines/dogfood/rules.json`, builds a review bundle, and fails the job when `eval` exits
non-zero. When the baseline branch or ancestor baseline does not exist yet (bootstrap), the gate
records an inconclusive assessment.
