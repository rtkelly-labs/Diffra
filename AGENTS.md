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

1. **Squash Merge Only**: All pull requests must be merged into `main` using **Squash and Merge** exclusively.
2. **Delete Branch on Merge**: Feature branches must be automatically deleted immediately upon merge into `main`.
3. **Linear History**: Maintain a strictly linear history. Rebase feature branches onto `main` before merging; no merge commits allowed.
4. **Direct Push Protection**: Direct pushes to `main` are blocked; PR mechanism required.
5. **Local Temp & Worktree Directory**: Keep temporary files and worktrees under `/temp/` (gitignored).
6. **Local Task Notes**: Keep local task notes in the ignored `TODO.md`.
7. **Auto-Merge Enabled**: PRs may enable auto-merge (squash) so they merge automatically once required checks pass.
8. **Pinned Action SHAs**: All GitHub Actions workflows MUST use 40-character commit SHAs instead of mutable tags.
9. **Script Language Policy**: Automation scripts in `scripts/` MUST use C# (`.cs`) executed via `dotnet run`. Python (`.py`), shell (`.sh`), and PowerShell (`.ps1`) scripts in `scripts/` are prohibited.
10. **External Actions**: Treat external package publication and issue creation as owner-authorized actions, as specified in `PLAN.md`.
