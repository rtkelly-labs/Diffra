# Parquet.SourceGenerator adopter slice

Parquet.SourceGenerator is the first Diffra adopter. Keep the protocol and all core types independent of Parquet names and assumptions. The integration reads the existing generated artifacts and emits a generic Diffra Evidence document.

## Evidence to collect

Start with one generator model and its existing artifacts:

- The generated `*.g.cs` file is the raw source artifact. Preserve its exact bytes and SHA-256 digest.
- The sibling `*.api.txt` file is a typed public API observation. Its format is ordinal-sorted, one public member per line, and omits implementation bodies.
- The sibling `*.api.shape.txt` can be included as a compact shape observation. Add `*.metrics.txt` only when metric scope and production toolchain are explicit.

Record repository identity, candidate commit, exact merge-base commit, producer and tool versions, schema digest, and artifact digests in the evidence and baseline reference. Do not infer a passing result from an absent companion file or an unsupported evidence type.

## Keep native gates required

Diffra adds review context and a durable evidence chain. It does not replace the adopter's existing checks:

- Exact generated-source golden comparison and Roslyn syntax/compilation validation remain required.
- Generated API baselines and the `PARQAPI001` build gate remain required.
- Shipped API and internal seam gates remain required where the change touches those surfaces.

Refreshing a `.g.cs` or `.api.txt` baseline cannot approve itself. Resolve the trusted baseline at the exact merge-base, produce candidate evidence, review the Delta and Assessment, and retain the normal build/API results as separate evidence. A Diffra failure or unavailable baseline cannot soften a native gate.

## Existing source locations

The golden and API checks live in `test/Parquet.SourceGenerator.Tests/GoldenCodeGenRegressionTests.cs`; the generated API renderer is `tools/Parquet.SourceGenerator.ApiGates/GeneratedApiBaseline.cs`. The emitted artifact contract is described in `docs/17-GENERATED-API-BASELINES.md`, and the API change contract is `docs/18-API-CHANGE-CONTRACT.md`. Golden refresh is wired through `.github/workflows/update-golden-files.yml` and remains an explicit reviewed operation.
