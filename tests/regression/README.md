# Diffra Regression Suite

The regression suite verifies that candidate builds of Diffra preserve canonical document
identities, schemas, exit codes, and outputs byte-for-byte against frozen corpora seeded from
earlier releases.

## Structure

- Each release has a frozen corpus directory under `tests/regression/<version>/` (e.g., `0.0.1-alpha.1/`).
- Inside each corpus directory:
  - `manifest.json`: declares every test case, command line arguments, input dependencies, expected outputs, expected exit codes, and generator provenance metadata.
  - `inputs/`: immutable input files (payloads, rules, committed evidence with generic environment, baseline references, manifests).
  - `expected/`: expected output documents, reports, and bundles produced by that release's official binary.
- The runner (`tests/regression/Diffra.Regression.csproj`) executes every case across all corpora, compares outputs byte-for-byte, validates backward reads via candidate CLI subprocess execution, and reports differences as RFC 6901 JSON pointers.

## Running the Suite Locally

### Candidate Tool Run

To run regression checks against a built candidate executable:

```sh
dotnet run --project tests/regression/Diffra.Regression.csproj --configuration Release -- --tool <path-to-diffra>
```

Missing `--tool` is an error (there is no implicit in-tree fallback). Bare tool names (e.g., `diffra`) are refused unless `--allow-path` is explicitly passed.

### Quick Mode (`--quick`)

When running regression checks during local development or rapid verification, use `--quick`:

```sh
dotnet run --project tests/regression/Diffra.Regression.csproj --configuration Release -- --quick --tool <path-to-diffra>
```

`--quick` mode performs a fast smoke-test:
- Skips old-vs-new comparison (`--compare`).
- Runs core candidate reproduction cases (`collect`, `diff`, `eval`, `report`).
- Skips slow multi-file directory generation cases (`present bundle` presentation materialization and `bundle import` CAS extraction).
- Validates fast document backward reads.

### Strict Comparison

Every run is strict; there is no opt-in flag. Only differences at exact JSON pointers listed in
`DeclaredBreakAllowList.cs` are accepted, and every other difference fails. The former `--strict`
flag has been removed, and passing it is an error.

### Old vs New Tool Comparison (`--compare`)

To verify that the candidate build and an earlier release agree on all corpus inputs:

```sh
dotnet run --project tests/regression/Diffra.Regression.csproj --configuration Release -- \
  --compare --old <path-to-released-diffra> --new <path-to-candidate-diffra> \
  --old-pkg <path-to-released-package> --new-pkg <path-to-candidate-package>
```

In `--compare` mode:
- The runner executes `--version` and inspects version metadata for both executables.
- Computes SHA-256 checksums of `--old-pkg` and `--new-pkg`.
- Asserts that both executables and packages are distinct builds, failing if identical.
- Executes old vs new comparison across all corpora.

## Output Comparison Semantics

### Exact Byte-for-Byte Verification
By default, all output files (JSON, HTML, Markdown, and binaries) must match expected files byte-for-byte (`SequenceEqual`).

### Directory Inventory Comparison
Directory outputs (`presentation/` and `imported-cas/`) are verified using an ordinal, case-sensitive inventory check (`StringComparer.Ordinal`).
- All relative paths in the actual directory must match the expected directory exactly.
- Extra files or missing files fail the comparison.
- All matching files are compared byte-for-byte.

### Zip Archive Comparison
Zip archives (`bundle.zip`) are compared by inspecting their decompressed contents:
- **Entry Inventory**: All entry paths inside the zip must match ordinally and case-sensitively (`StringComparer.Ordinal`).
- **Entry Count**: The number of entries in expected and actual archives must be identical.
- **Decompressed Bytes**: Every decompressed entry stream is compared byte-for-byte (`SequenceEqual`).
This entry-by-entry comparison ensures deterministic content verification while allowing archive creation timestamps and compression header variations to exist across zip implementations.

### Delta Runtime Provenance and Identity (`PR #115`)

Previously, `diffra diff` recorded the host .NET runtime version (`System.Environment.Version`) in `delta.provenance.environment.runtime`, which was included in the canonical SHA-256 `DeltaDocument.Id`. This caused Delta IDs to vary across machines or environments with different .NET patch versions.

In **PR #115**, the host runtime version was dropped from the default comparator environment and pinned to invariant `{"runtime": "dotnet"}`. As a result:
- **Single-Tool Determinism**: Within a single tool version tested on a single machine, Delta IDs are 100% deterministic and byte-identical across runs. The regression runner strictly asserts this determinism by executing candidate diff cases repeatedly and requiring byte-identical Delta output documents.
- **Corpus 0.0.1-alpha.1 Preservation**: The frozen `0.0.1-alpha.1` corpus remains the immutable record of what the official `0.0.1-alpha.1` release produced (with patch-version strings such as `10.0.14`).
- **Declared Breaking Change**: When evaluating candidate builds against the `0.0.1-alpha.1` corpus or comparing old vs new tools, the runner strictly validates all semantic fields (changes, metrics, comparator identity, and evidence references). The resulting ID and environment mismatch is classified and reported as:
  ```
  DECLARED-BREAK delta-identity-runtime (PR #115)
  ```
  This exemption applies exclusively to Delta documents whose expected environment contains a patch-version runtime string. Every other ID mismatch causes the test to fail immediately.
- **Strict Backward Reads**: Candidate builds must still parse, read, and strictly verify (`DocumentIdentity.Verify`) historical `0.0.1-alpha.1` Delta documents without error.

### Host Environment Variance and Strict Allow-List

`diffra collect` records host platform, runtime, and SDK versions in Evidence documents. Rather than loose normalization, all cross-platform variances are explicitly verified against committed rules in `DeclaredBreakAllowList.cs`:
- Only four exact JSON pointers may differ: `/environment/platform`, `/environment/runtime`, `/environment/tools/0/version`, and the resulting `/id`. Each is permitted only when it passes its validator.
- `/environment/tools/0/version` is accepted only when both documents hold exactly one tool entry, `{"id":"dotnet-sdk","version":<string>}`. A change to the tools array length, to a tool `id`, or a key added to or removed from a tool entry fails.
- Evidence payload digests and contents must match byte-for-byte.
- Candidate Evidence documents must pass self-identity verification (`CanonicalHashHelper.ComputeEvidenceId` matching stored `id`).
- Strict verification is always active: any difference at an unlisted JSON pointer causes the regression suite to fail immediately.

### Declared Breaks and Explicit Allow-List

Declared breaks must never hide regressions. Rather than blanket-skipping document comparison, every declared break must be explicitly registered on a committed allow-list (`DeclaredBreakAllowList.cs`). Each allow-list entry specifies:
- **Document Kind**: the protocol document type (e.g., `delta`, `evidence`).
- **Exact JSON Pointer**: the precise path where variance is expected (e.g., `/provenance/environment/runtime`, `/id`).
- **Reason and Reference**: the rationale and PR introducing the change (e.g., `DECLARED-BREAK delta-identity-runtime (PR #115)`).
- **Rule Validator**: programmatic validation ensuring the difference matches the exact expected variance.

Comparison of every other field in the document continues strictly. Any difference outside the allow-list (such as a changed comparator that also corrupts `changes`) fails immediately. All unverified differences fail the build.

## Adding a Corpus for a New Release

To freeze a new corpus for release `<version>` using a verified released binary:

```sh
dotnet run --project tests/regression/Diffra.Regression.csproj -- generate <version> --tool <path-to-released-diffra>
```

`generate` strictly requires a verified external release executable and refuses `.csproj` or in-tree source paths. Before it writes anything, it checks that the tool reports `<version>` (an unknown version fails) and derives the SHA-256 from the release package (`artifacts/package/` or `temp/pinned-tool/`) or, failing that, from the executable itself. It records that provenance (asset name, SHA-256, version) in `manifest.json`.

Committed Evidence inputs under `inputs/evidence/` are rewritten by the harness, not kept as the release emitted them: `environment` is replaced with `{"platform":"generic","runtime":"10.0","tools":[]}` and `id` is re-minted with `CanonicalHashHelper.ComputeEvidenceId`, so the fixtures carry no host fingerprint.
