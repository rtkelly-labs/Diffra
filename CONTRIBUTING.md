# Contributing to Diffra

Diffra is being developed in phases described in `PLAN.md`. Before implementing a phase, read the
matching goals, constraints, and completion criteria there, along with the relevant design in
`VISION.md`.

---

## Code of Conduct

All contributors and maintainers are expected to follow the [Code of Conduct](CODE_OF_CONDUCT.md) in all interactions.

---

## Development

Install the exact SDK version from `global.json`. Build and run tests with:

```bash
dotnet build Diffra.slnx --configuration Release
dotnet run --project tests/Protocol.Tests/Protocol.Tests.csproj --configuration Release --no-build
dotnet run scripts/RunDogfood.cs
```

Keep the core independent of adopter-specific concepts and external runtime frameworks. Use the
existing BCL-first approach for the foundation; propose dependencies where they solve a specific,
documented need.

---

## Development Workflow

1. **Branching & Pull Requests**:
   - Create a feature or bugfix branch using clear naming:
     - `feat/<feature-name>`
     - `fix/<bug-description>`
     - `docs/<doc-update>`
     - `chore/<maintenance-task>`
   - Submit changes through a pull request.
   - Add tests with behavior changes, and update the phase documentation when a design decision changes the protocol, identity rules, trust model, or public command contract.
   - All pull requests are merged exclusively via **Squash and Merge**.

2. **Linear History & Rebase**:
   - We enforce a strictly linear history on `main`.
   - Before opening a PR or marking it ready for review, rebase your branch on latest `main`:

     ```bash
     git fetch origin
     git rebase origin/main
     ```

3. **Temporary Files & Policies**:
   - Use `/temp/` for local temporary files and worktrees.
   - Do not publish packages or create external issues until the repository owner authorizes those actions.

---

## Reporting Issues

- **Bug Reports**: Please use the [Bug Report](.github/ISSUE_TEMPLATE/bug_report.yml) form. Provide exact reproduction steps and system versions.
- **Feature Requests**: Please use the [Feature Request](.github/ISSUE_TEMPLATE/feature_request.yml) form.
