# Diffra

Diffra is an independent .NET repository for a tool that records the signals CI produces and
compares a pull request with the commit it branched from and with recent history. Read
`docs/vision.md` for the current vision, `docs/adr/` for the decision records, `docs/glossary.md`
for the vocabulary, and `PLAN.md` for the active implementation sequence. The root `VISION.md`
preserves the original design handover and no longer describes the design.

## Operating Principles

- **Diffra Uses Diffra**: Diffra is used to build Diffra. This repository installs Diffra the way an adopter does: the same root file, Action and collecting job, within the adopter's wiring limit, with enforcement turned on only through the adopter's opt-in setting. Capabilities no adopter has yet run in a separate lane that never fails the build. Converters are benchmarked here on real input. Until that path exists, the self-evaluation scripts in `scripts/` stand in for it.
- **Surface, Do Not Enforce**: Diffra reports findings; it does not manage workflow. Its step does not fail a build unless the adopter opts in, and it has no approval or promotion step.
- **Tool First, Protocol as the Means**: Record formats, schemas and comparison rules stay generic and tied to no adopter or CI vendor, but they serve the tool rather than stand as the product.
- **Limited Package References**: Each package reference is reviewed. Libraries that pull in large dependencies stay out of the core and live in separate packages built from this repository (`docs/adr/0009-dependencies-and-extensions.md`).
- **Keep the Vision True**: `docs/vision.md` describes the project as it actually is, not as it was planned. Any pull request that changes scope, behaviour or a decision record updates it in the same change, including its "What exists today" section. The root `VISION.md` is history and is not updated.
- **Zero Warnings, Zero Regressions**: The codebase enforces `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` and deterministic output.

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
