# Diffra

Diffra is an independent .NET repository for typed comparison and trusted baselines. Read
`docs/vision.md` for the current vision and `PLAN.md` for the active implementation sequence.
The root `VISION.md` preserves the original design handover.

## Operating Principles

- **Diffra Utilizes Diffra (Self-Verification)**: The tool is its own first and continuous client. Utilizing Diffra to build, test, baseline, evaluate, and review Diffra is a core operating principle. Every capability introduced into the system (metrics, bundles, DAG graph execution, baseline resolvers, external adapters) must be actively incorporated into Diffra's own development, test, and CI gating workflow.
- **Protocol Before Frameworks**: The core protocol, schemas, and documents remain generic, independent, and free of heavyweight runtime dependencies.
- **Zero Aspire Runtime Dependencies**: Diffra has no runtime framework dependency on Aspire (estate invariant).
- **Zero Warnings, Zero Regressions**: The codebase enforces `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` and deterministic canonical identity.

## Build and validation

- Use the SDK pinned in `global.json`.
- Build with `dotnet build Diffra.slnx --configuration Release`.
- Run the protocol checks with `dotnet run --project tests/Protocol.Tests/Protocol.Tests.csproj --configuration Release`.
- Run Diffra's self-evaluation pipeline with `dotnet run scripts/RunDogfood.cs`.
- Keep protocol code and schemas generic; Parquet.SourceGenerator is an adopter, not a dependency.

## Repository workflow

- Use pull requests with squash merges and linear history. Do not commit directly to the primary branch.
- Keep temporary files and worktrees under `/temp/`; keep local task notes in the ignored `TODO.md`.
- Treat external publication and issue creation as owner-authorized actions, as specified in `PLAN.md`.
