# Diffra Regression Suite

The regression suite verifies that candidate builds of Diffra preserve canonical document
identities, schemas, exit codes, and output byte-for-byte against frozen corpora seeded from
earlier releases.

## Structure

- Each release has a frozen corpus directory under `tests/regression/<version>/` (e.g., `0.0.1-alpha.1/`).
- Inside each corpus directory:
  - `manifest.json`: declares every test case, command line arguments, input dependencies, expected outputs, and expected exit code.
  - `inputs/`: immutable input files (payloads, rules, committed evidence, baseline references, manifests).
  - `expected/`: expected output documents, reports, and bundles produced by that release's official binary.
- The runner (`tests/regression/Diffra.Regression.csproj`) executes every case across all corpora, compares outputs byte-for-byte, validates backward reads, and reports differences as RFC 6901 JSON pointers.

## Running the Suite Locally

### Run candidate build against all corpora

```sh
dotnet run --project tests/regression/Diffra.Regression.csproj --configuration Release -- --tool <path-to-diffra>
```

`<path-to-diffra>` may be an installed executable binary, a `.dll` (executed via `dotnet <dll>`), or `src/Cli/Diffra.Cli.csproj`.

### Run old-vs-new tool comparison

To verify that the candidate build and an earlier release agree on all corpus inputs:

```sh
dotnet run --project tests/regression/Diffra.Regression.csproj --configuration Release -- \
  --compare --old <path-to-released-diffra> --new <path-to-candidate-diffra>
```

## Declared Breaks

Diffra guarantees deterministic identity and backward compatibility. If a release intentionally
introduces a semantic change to evaluation or comparison, it must bump the corresponding version:

- **Comparator Version**: declared in `comparator.version` of a Delta document.
- **Evaluator Version**: declared in `provenance.version` of an Assessment document.

When the regression runner observes that an expected document's comparator or evaluator version
differs from the candidate's output, that case is classified as a **declared break**. Declared
breaks are recorded and displayed in the run summary, but do not fail the regression suite. The
versions are read dynamically from the output documents without hard-coded constants.

## Adding a Corpus for a New Release (One Command)

To freeze a new corpus for release `<version>` using the newly released `diffra` executable:

```sh
dotnet run --project tests/regression/Diffra.Regression.csproj -- generate <version> --tool <path-to-released-diffra>
```

For example:
```sh
dotnet run --project tests/regression/Diffra.Regression.csproj -- generate 0.0.2 --tool temp/bin/diffra
```

This single command:
1. Prepares the full variety of input payloads and rules (including empty metrics, unicode keys, negative and zero values, large numbers, and incompatible schemas).
2. Generates canonical baseline evidence, baseline references, and manifests.
3. Executes the released `diffra` tool across `collect`, `diff`, `eval`, `report`, `bundle`, and `present` commands.
4. Materializes expected outputs and records `manifest.json`.
