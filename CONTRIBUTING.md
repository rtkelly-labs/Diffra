# Contributing

Diffra is being developed in phases described in `PLAN.md`. Before implementing a phase, read the
matching goals, constraints, and completion criteria there, along with the relevant design in
`VISION.md`.

## Development

Install the exact SDK version from `global.json`. Build and run tests with:

```sh
dotnet build Diffra.slnx --configuration Release
dotnet run --project tests/Protocol.Tests/Protocol.Tests.csproj --configuration Release --no-build
```

Keep the core independent of adopter-specific concepts and external runtime frameworks. Use the
existing BCL-first approach for the foundation; propose dependencies where they solve a specific,
documented need.

## Changes

Submit changes through a pull request. Keep history linear and use squash merges. Add tests with
behavior changes, and update the phase documentation when a design decision changes the protocol,
identity rules, trust model, or public command contract.

Use `/temp/` for local temporary files and worktrees. Do not publish packages or create external
issues until the repository owner authorizes those actions.
