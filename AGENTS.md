# Diffra

Diffra is an independent .NET repository for typed comparison and trusted baselines. Read
`docs/vision.md` for the current vision and `PLAN.md` for the active implementation sequence.
The root `VISION.md` preserves the original design handover.

## Build and validation

- Use the SDK pinned in `global.json`.
- Build with `dotnet build Diffra.slnx --configuration Release`.
- Run the protocol checks with `dotnet run --project tests/Protocol.Tests/Protocol.Tests.csproj --configuration Release`.
- Keep protocol code and schemas generic; Parquet.SourceGenerator is an adopter, not a dependency.
- Add no runtime framework dependency on Aspire.

## Repository workflow

- Use pull requests with squash merges and linear history. Do not commit directly to the primary branch.
- Keep temporary files and worktrees under `/temp/`; keep local task notes in the ignored `TODO.md`.
- Treat external publication and issue creation as owner-authorized actions, as specified in `PLAN.md`.
