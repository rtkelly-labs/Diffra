# Diffra

Diffra records typed software evidence, compares it with an exact baseline, evaluates the change under separate policy rules, and renders a review report. It is an independent protocol and .NET CLI. Parquet.SourceGenerator is the planned first adopter; it is not a dependency.

In testing terms, Diffra is differential testing against a trusted baseline: snapshot and approval testing with typed comparison, a verdict kept separate from the measured change, and a baseline that the change under review cannot approve for itself. [Diffra in testing terms](docs/testing-concepts.md) explains the idea.

The CLI handles structured numeric Evidence with a declared scope and methodology. `collect`, `diff`, `eval`, and `report` are separate file operations; `bundle` moves evidence through a content-addressed store and `present` builds and serves a review bundle. Evidence, Delta, Assessment, and presentation each have their own identity. The [CLI reference](docs/cli.md) covers every command, and the [feature status](docs/README.md#feature-status) table shows which capabilities are CLI commands, which are library APIs only, and which are planned. The original [handover vision](VISION.md) and [plan](PLAN.md) preserve the project brief.

## Install

Diffra is distributed as a .NET tool package and as standalone self-contained executables for supported platforms.

### .NET Tool (`Diffra.Cli`)

Adopters can pin `Diffra.Cli` using a local repository tool manifest (`.config/dotnet-tools.json`):

```sh
dotnet new tool-manifest # if not already present
dotnet tool install Diffra.Cli --version 0.0.1
```

Invoke the tool with:

```sh
dotnet diffra --help
```

> **Runtime requirement**: The `Diffra.Cli` tool requires the .NET 10 SDK and the `Microsoft.AspNetCore.App` shared runtime for serving local review bundles (`diffra present serve`). The ASP.NET Core shared runtime is included with standard .NET SDK and ASP.NET Core runtime installations, but is not present in runtime-only (.NET Runtime) installations.

### Release archives (standalone binary)

Standalone, self-contained single-file archives are published for `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, and `win-x64`. These binaries include the runtime dependencies and do not require an installed .NET runtime.

Download the archive and `SHA256SUMS` from the release:

```sh
# Example for linux-x64
curl -sSLO https://github.com/rtkelly-labs/Diffra/releases/download/v0.0.1/diffra-0.0.1-linux-x64.tar.gz
curl -sSLO https://github.com/rtkelly-labs/Diffra/releases/download/v0.0.1/SHA256SUMS

# Verify checksum
sha256sum --check --ignore-missing SHA256SUMS
# On macOS: shasum -a 256 -c --ignore-missing SHA256SUMS

# Extract and run
tar -xzf diffra-0.0.1-linux-x64.tar.gz
./diffra --help
```

## Build and check

Install the .NET SDK pinned in `global.json`, then run:

```sh
dotnet build Diffra.slnx --configuration Release
dotnet run --project tests/Protocol.Tests/Protocol.Tests.csproj --configuration Release --no-build
```

The check runner has no NuGet test dependencies. CI builds the solution and runs that same executable check suite. It verifies canonical identity, comparison and policy separation, CLI errors, report escaping and repeatability, and the checked-in [sample workflow](examples/sample-project/workflow/).

## Try the file workflow

From the repository root:

```sh
mkdir -p temp/demo
dotnet run --project src/Cli/Diffra.Cli.csproj -c Release --no-build -- collect \
  --payload examples/sample-project/workflow/candidate-payload.json \
  --subject-id example/project --commit 2222222222222222222222222222222222222222 \
  --type-id org.example.metrics --type-version 1.0 \
  --producer-id org.example.fixture --producer-version 1.0 \
  -o temp/demo/evidence.json
dotnet run --project src/Cli/Diffra.Cli.csproj -c Release --no-build -- diff \
  --baseline-ref examples/sample-project/workflow/baseline-reference.json \
  --baseline-evidence examples/sample-project/workflow/baseline-evidence.json \
  --evidence temp/demo/evidence.json -o temp/demo/delta.json
dotnet run --project src/Cli/Diffra.Cli.csproj -c Release --no-build -- eval \
  --rules examples/sample-project/workflow/rules.json \
  --delta temp/demo/delta.json -o temp/demo/assessment.json
dotnet run --project src/Cli/Diffra.Cli.csproj -c Release --no-build -- report \
  temp/demo/delta.json --assessment temp/demo/assessment.json \
  --baseline-ref examples/sample-project/workflow/baseline-reference.json \
  --format html -o temp/demo/report.html
```

The sample reference simulates a trusted resolver. Its issuer is `fixture-only`. The current local `diff` command checks the reference's shape and digest, but does not authenticate the issuer; do not accept a PR-supplied reference as a CI trust decision. The Git merge-base resolver, two-run promotion records, and graph execution exist as library APIs but are not yet wired into the CLI; the Parquet integration is planned. Native Parquet golden, compilation, and API gates must remain required when that integration is added.

`eval` writes an Assessment before returning exit code 3 for `fail`, `needs-review`, or `inconclusive`. Invalid input returns 2; file or execution errors return 1. A report without an Assessment states that it has no policy verdict.
