# Goal: a tiered test foundation, with a regression gate on every release

Diffra is tested in three tiers: shallow, deep and regression. No release can be cut unless the
deep and regression tiers pass on the exact commit being tagged. Diffra also gates its own test
strength with Diffra.

## Current state (main, after v0.0.1-alpha.1)

- **Every PR** (`ci.yml`):
  - build;
  - `tests/Protocol.Tests` (custom runner, 13 test files);
  - schema fixtures;
  - property tests (`tests/Protocol.Properties`);
  - the canonical-JSON differential test (`tests/CanonicalJson.Differential`);
  - a smoke test of the packed tool;
  - dogfood (`scripts/RunDogfood.cs`).
- **Folded into deep tier (formerly standalone workflows):**
  - fuzzing (`tests/Protocol.Fuzz`, 30 minutes per target by default);
  - mutation testing (Stryker across all of Protocol with 57 floor, and break threshold 80 on Comparison and Assessment).
  - standalone `fuzz.yml` and `mutation.yml` are retired so PRs stay strictly shallow.
- **Gaps:**
  1. Everything runs on ubuntu-latest. The macOS and Windows archives ship without being tested on those systems.
  2. `release.yml` runs only the unit tests. A tag can ship without fuzzing, mutation or property tests having passed on that commit.
  3. Nothing checks that a new version produces the same canonical documents and IDs as an earlier version. Stable identity is Diffra's core promise.
  4. Dogfood uses the PR's own build as the judge and compares against a hand-committed baseline with zeroed commits.

## Tiers

| Tier | When | Contents | Time budget |
|---|---|---|---|
| Shallow | Every PR (required checks) | build, unit tests, schema fixtures, short property and differential runs, packed-tool smoke test, dogfood gate with the pinned judge | ≤ 10 min |
| Deep | Nightly on main, on demand, and before a release | fuzzing for every target; mutation testing across all of Protocol (floor 57 ratcheting upward, 80 gate on Comparison and Assessment); high-iteration property runs; unit tests and the CLI end-to-end suite on Linux; five-platform native archive smoke matrix (Linux x64/arm64, macOS arm64/x64, Windows x64) | ≤ 60 min |
| Regression | Before a release, and nightly | frozen corpus, backward reads, old vs new tool comparison (below) | ≤ 15 min |

## Regression suite

- **Frozen corpus.**
  - Each release freezes `tests/regression/<version>/`.
  - It holds input payloads plus the expected Evidence, Delta, Assessment and report outputs, including their IDs.
  - A new build must reproduce every earlier corpus byte for byte. A corpus is exempt only if its `EvaluatorVersion` or comparator version was bumped on purpose, with the CHANGELOG entry marking it breaking.
- **Backward reads.** The current tool must read and validate every document that any earlier version wrote.
- **Old vs new tool.** The pinned released tool and the candidate build run the same inputs, and Diffra compares their outputs. Any difference must be declared through a version bump.
- **First corpus.** Seed it from v0.0.1-alpha.1.

## Release gate

- `release.yml` no longer runs its own copy of the tests. It calls the deep and regression workflows as reusable workflows (`workflow_call`), and requires them on the tagged commit.
- If either fails, no GitHub Release is created.
- The manual dry run (`workflow_dispatch`) takes the same path. "Dry run green" therefore means "ready to release".

## Diffra testing Diffra

- **Pinned judge.**
  - CI installs a fixed released Diffra.Cli, currently 0.0.1-alpha.1, from the GitHub Release. It checks SHA256SUMS first.
  - The judge is pinned in `.config/dotnet-tools.json`. A PR's own build never judges itself.
- **Baselines.**
  - Each push to main records Evidence on a `diffra-baselines` branch at `evidence/<sha>.json`. Only that workflow can write there.
  - A PR compares against the Evidence of its merge-base.
  - A PR reports `inconclusive` instead of failing when no baseline exists yet.
- **Deep-run results become Evidence:**
  - test count;
  - mutation score;
  - fuzz execution rate and findings;
  - coverage;
  - archive and package sizes.

  These are gated against the main baseline, so weaker tests count as a regression, not just a red test.

## Order of work

Each step is one PR.

1. **Document the tiers.** Add them to `docs/artifact-delivery-and-ci.md`, and make the shallow tier the required check set.
2. **Dogfood gate.** Use the pinned judge, with baselines produced by main.
3. **Regression suite.** The frozen corpus seeded from 0.0.1-alpha.1, backward reads, and the old vs new tool comparison.
   Status: complete (seeded from 0.0.1-alpha.1).
4. **Deep workflow.** An OS matrix, a nightly schedule, and mutation testing across all of Protocol.
   Status: implemented in `.github/workflows/deep.yml` with five-platform native archive smoke matrix (Linux x64/arm64, macOS arm64/x64, Windows x64), long-budget parallel fuzzing, full-scope Protocol mutation testing (floor 57 for all of Protocol, ratcheting upward over time; 80 gate on Comparison and Assessment), 100k property checks, and machine-readable `deep-summary.json`.
5. **Release gate.** `release.yml` requires the deep and regression workflows on the tagged commit.
   Status: implemented in `.github/workflows/release.yml`; a tag release also requires release notes, and its own corpus `tests/regression/<version>/` is added afterwards from the published package (see "Releasing Diffra" in `docs/artifact-delivery-and-ci.md`).
6. **Test strength as Evidence.** Turn the deep-run results into Evidence and gate on them.
   Status: implemented. `scripts/DeepEvidence.cs` turns `deep-summary.json` into strength, mutation, throughput and size Evidence (payload schema in `baselines/deep/payload.schema.json`, rules in `baselines/deep/*.rules.json`), and the test job now collects coverage. The nightly run on `main` records the Evidence, and `deep-baseline.yml` publishes it to `diffra-baselines` at `evidence/deep/<sha>/`. Every other deep run is judged against the nearest ancestor's Evidence with the pinned judge, and a `fail` verdict fails the `deep` job. With no baseline the verdict is `inconclusive`. See `docs/artifact-delivery-and-ci.md`.

## Done when

- A PR gets the shallow tier only, in 10 minutes or less.
- Nightly main runs deep (native archive publish, isolated smoke test, and regression corpus check across all five platforms; heavy jobs on Linux) and regression.
- Pushing a `v*` tag cannot produce a release unless deep and regression have passed on that commit.
- Every released version has a frozen regression corpus that later builds reproduce.
- The dogfood gate uses a pinned released judge and baselines produced by main.

## Constraints

- All tooling is C# run with `dotnet run`. No bash or Python files; inline shell in workflow YAML is fine.
- Pin actions to full commit SHAs.
- Workflows default to `contents: read`.
- Use plain `pull_request` for PR workflows, never `pull_request_target`.
- Zero warnings.
- Pull requests use conventional titles and squash auto-merge.
- Releases and NuGet publishing remain owner actions.
