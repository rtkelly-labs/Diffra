# CLI reference

The `diffra` CLI has six commands. Each reads files, validates them, and writes one output
atomically; no command overwrites one of its own inputs. This page describes the commands and the
two hand-written input files, the metrics payload and the rules file. The document shapes the
commands write are in the [protocol](protocol.md).

Run the CLI from a build with `dotnet run --project src/Cli/Diffra.Cli.csproj -c Release --no-build -- <command>`.
`diffra --help` prints the usage line for every command; `collect`, `bundle` and `present` also
accept `--help`.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | The command completed. For `eval`, the Assessment outcome is `pass` or `warn`. |
| 1 | A file or execution error, such as an unreadable path. |
| 2 | Invalid input: an unknown or repeated option, a malformed document, or a digest mismatch. |
| 3 | `eval` only: the Assessment outcome is `fail`, `needs-review` or `inconclusive`. The Assessment is still written. |

`diff` does not use the exit code to report change. A changed and an unchanged comparison both exit
0; the Delta's `status` records which.

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
  [--format html|json|markdown] [-o <file>]
```

The default format is HTML; without `-o` the report goes to standard output. A report without an
Assessment says it has no policy verdict. Evidence-derived text is escaped, and the HTML loads no
scripts or remote assets.

## `bundle`

Moves evidence between machines through a portable archive and a local content-addressed store.

```sh
diffra bundle export --manifest <file> -o <bundle.zip> [--store <cas-dir>] [--artifacts-dir <dir>] \
  [--delta <file>] [--assessment <file>] [--baseline-ref <file>]
diffra bundle import --bundle <bundle.zip> --store <cas-dir>
```

Import verifies every digest before writing into the store and rejects entries that would escape
the target directory. The manifest and directory layout are described in
[artifact delivery and CI](artifact-delivery-and-ci.md).

## `present`

Builds a self-contained review bundle and serves it locally.

```sh
diffra present bundle --delta <delta.json> -o <output-dir> [--assessment <file>] [--baseline-ref <file>] \
  [--manifest <file>] [--artifacts-dir <dir>] [--store <cas-path>]
diffra present serve <bundle-directory> [--port <port>]
```

`serve` listens on `127.0.0.1` only (port 5055 by default), rejects requests for any other host,
and sends a strict Content-Security-Policy.

## Self-verification

Diffra runs this CLI on itself in CI. `dotnet run scripts/RunDogfood.cs` collects metrics about the
repository, diffs them against the checked-in baseline in `baselines/dogfood/`, evaluates them
under `baselines/dogfood/rules.json`, builds a review bundle, and fails the job when `eval` exits
non-zero. The baseline there is a committed fixture, not one resolved by the merge-base resolver.
