# Artifact delivery, local presentation, and CI

Status: design proposal, 27 September 2026. The current CLI implements
`collect`, `diff`, `eval`, and single-file `report`. The commands and delivery
documents below are planned; they are not available yet.

## Decision

Diffra will prepare a complete presentation directory before opening a report.
The CLI fetches referenced evidence and artifacts with the caller's credentials,
checks every digest, and writes HTML with relative links to local files. Images
and large binaries remain separate files. A browser never needs a GitHub token or
a private release URL.

The same directory can be served by the CLI through ASP.NET Core on loopback.
The rendered site can also be copied to static hosting when its contents are
public. A private report stays local or goes to an access-controlled host;
`docs.ryankelly.dev` is a public documentation site, not a store for private
evidence.

## Source and identity

The [protocol manifest](../schemas/manifest-v1.schema.json) lists semantic
artifacts and Evidence IDs. Each artifact has a stable ID, SHA-256 digest, media
type, byte length, and safe relative path. Evidence, Delta and Assessment
documents retain their own identities. Materializing or rendering a report does
not change those identities.

Add a separate, versioned delivery index for transport. It maps each required
document or artifact digest to a source such as a GitHub Release asset ID, or a
future object-store key. It records the repository, immutable release ID and tag,
source commit, expected size, and digest. The delivery index receives its own
digest. A mutable `latest` selector may help a person discover a release, but the
resolved index and presentation inventory record exact IDs. CI requires an
explicit tag or index digest.

A release asset is a storage location, not a statement about the records it
holds. Which record a run is compared with is set by the comparison point
([0003](adr/0003-comparison-history-and-noise.md)), not by where the record is
stored, and there is no approval step ([0005](adr/0005-surface-do-not-enforce.md)).
The CLI rejects a delivery index that disagrees with the protocol manifest or
the bytes it fetches.

## CLI contract

The proposed command is:

```sh
diffra present fetch \
  --repo owner/private-project \
  --release v1.2.3 \
  --output ./review-bundle \
  --serve
```

`present fetch` resolves the named release, downloads its delivery index, then
streams required assets into a temporary directory. It checks the index,
manifest, document identities, declared sizes, and SHA-256 digests before an
atomic move into `--output`. It reuses a verified content-addressed cache when
available. An optional `--offline` mode accepts only cached bytes that still
match the index. A failed required download leaves no successful bundle. Optional
artifacts stay listed with an explicit unavailable state.

`--serve` starts after preparation succeeds. `diffra present serve ./review-bundle`
serves an existing verified directory without fetching. Both commands print the
local URL and stop on Ctrl-C. The existing `report` command stays a pure renderer
for small document-only reports; presentation preparation composes that renderer
with verified local artifact paths.

For a private GitHub repository, use a GitHub App installation token or a
fine-grained token with repository Contents read permission. In same-repository
Actions jobs, use a read-only `GITHUB_TOKEN`. A local CLI may obtain a token from
`GH_TOKEN` or the user's `gh` credential helper. It must not print a token, put
one in the output folder, or place one in an HTML URL. The asset endpoint may
return bytes or a redirect; the client must handle both and must not forward an
Authorization header to an unrelated host. Test that redirect behavior.

## Directory contract

`--output` is a self-contained presentation unit with a stable layout:

```text
review-bundle/
  index.html
  diffra-delivery-index.json
  inventory.json
  documents/
    manifest.json
    comparison-evidence.json
    candidate-evidence.json
    delta.json
    assessment.json
  assets/
    sha256/
      <digest>
```

`inventory.json` maps human artifact IDs and names to digest-named local files.
The report links to those files through relative paths. It shows images inline
where the media type and renderer permit it and offers all other assets as
downloads. Artifact bytes are never embedded as data URLs. Missing optional
assets remain visible as missing; the report cannot imply that they were checked.

Sort inventory entries and omit fetch timestamps, machine paths, and temporary
names from deterministic output. Preserve source metadata in the delivery index.
Two preparations of the same index and bytes should produce byte-identical
documents and HTML, even when they run in different directories.

Treat names and paths in fetched documents as untrusted. Reject absolute paths,
`..`, duplicate or case-colliding paths, symlinks that escape the output root,
unexpected media types for inline display, and declared sizes above configured
limits. Download into temporary files with bounded streaming and hash while
writing. Do not unpack archives by default. If a provider supports range reads,
resume only after revalidating the immutable source and partial bytes; otherwise
restart the file. A failed digest never enters the cache or final directory.

## Local server

Use an ASP.NET Core host inside the CLI, with a physical file provider rooted at
the prepared presentation directory. Bind to `127.0.0.1` on an assigned port by
default. Reject unexpected Host headers, disable directory browsing, and expose
only the generated report and declared assets. Serve images and large binaries
with correct content types and byte ranges. Set a restrictive Content Security
Policy so the report loads its own files and makes no remote requests. Do not
make remote binding an accidental consequence of `ASPNETCORE_URLS`; an explicit
opt-in and warning are required for non-loopback access.

The host does not compare or evaluate records, download files, or mutate documents during a
request. `present fetch` completes verification first. An offline test should
open the site, inspect an image, and download a binary without network access.

## Storage choices

GitHub Release assets are the first durable transport for versioned presentation
bundles. GitHub currently permits up to 1,000 assets per release, with each file
under 2 GiB and no release-wide size or bandwidth cap. A file above that limit
needs a versioned chunk list with per-chunk and whole-file hashes, or an
object-store adapter. The CLI must not require one giant archive. Individual
assets let it fetch only the files the reviewer requests, while `present fetch`
defaults to a complete bundle for offline review.

GitHub Actions artifacts are temporary handoff between jobs and for PR review.
Their default retention is 90 days and they can expire or be deleted with a run.
They are not the permanent source for a release. Git LFS is not the presentation
transport; it would tie binary retrieval to repository checkout and plan-specific
storage limits. Keep source-controlled fixtures small.

The delivery provider interface should separate resolving an immutable index
from streaming one digest. A later cloud provider can use object storage with
short-lived OIDC credentials in CI and signed requests for authorized reviewers.
The protocol manifest and digest checks remain the same across providers.

## CLI distribution

Ship one versioned CLI through two outputs from the same source commit:

| Output | Use | Constraint |
| --- | --- | --- |
| Self-contained, single-file archive for each supported runtime identifier | Local reviewers and CI machines without a preinstalled runtime | Larger files; rebuild for runtime security updates; test each platform |
| `Diffra.Cli` .NET tool package | .NET repositories with a pinned local tool manifest | Requires a compatible .NET SDK and ASP.NET Core shared runtime for `--serve` |

Start with `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, and `win-x64` as
release targets. Publish an archive per target, the `.nupkg`, SHA-256 checksum
file, and source commit metadata on a tagged GitHub Release. Test both an
installed tool and the extracted self-contained executable. Avoid trimming and
Native AOT in the first release; add either only after the fetcher, renderer, and
ASP.NET host pass platform tests. A local tool manifest in adopters pins the
package version. Global installation remains available for interactive use.

## CI and release flow

An adopter's pull request workflow runs its native build and tests, produces
Evidence and artifacts, compares with the record at its comparison point on the
target branch, evaluates the rules, and uploads a temporary review bundle. It
uses read-only repository permissions, so it cannot change the records stored
for target-branch commits. Required
native gates remain required. A neutral or incomplete Diffra result must not
turn a failed native gate green.

The release workflow starts from a protected tag or approved environment. It
rebuilds and tests the exact commit, verifies the CLI package and per-platform
archives, writes checksums and a delivery index, and uploads assets to a draft
GitHub Release. It publishes the release only after asset and digest checks
pass. Only the publishing job receives Contents write permission. Pin workflow
actions to full commit IDs. For cloud storage, exchange the job's OIDC identity
for short-lived credentials scoped to the repository, workflow, and release
environment. PR jobs receive no cloud write role.

Use the same `present fetch` validation in CI against a local fixture and, after
upload, against the draft release. The latter catches broken asset IDs or
missing files before the release becomes the documented source. A release index
records its source tag and commit. Publication on `docs.ryankelly.dev` covers
public product documentation; release assets carry downloadable CLI binaries,
and private adopter bundles stay behind repository or cloud access controls.

## Diffra's own test tiers

Diffra tests itself in three tiers. The plan and its order of work are in
[plans/test-foundation.md](plans/test-foundation.md).

| Tier | When | Gate |
| --- | --- | --- |
| Shallow | Every pull request and push to `main` (`ci.yml`) | Required. The `build` check passes only when every shallow job passes. |
| Deep | Nightly on `main`, on demand, and before a release (`deep.yml`) | Five-platform archive smoke matrix, full-scope Protocol mutation testing (floor 57 ratcheting upward, 80 gate on Comparison and Assessment), fuzzing across all targets, 100k property runs, and unit/e2e checks with coverage. Aggregated into `deep-summary.json`, then collected as Diffra Evidence and gated against main's deep baseline (see [Deep test strength as Evidence](#deep-test-strength-as-evidence)). A release requires it on the tagged commit. |
| Regression | Nightly on `main` and before a release | Frozen corpora from earlier releases must reproduce, and the released and candidate tools must agree. A release requires it on the tagged commit. |

The shallow tier is the build and unit tests, the schema fixtures, the
canonical JSON differential test, the property tests, the smoke test of the
packed tool, and the dogfood gate. It stays under ten minutes. A new shallow job
joins the `needs` list of the `build` job in `ci.yml`, so the required check
covers it without a ruleset change. The deep tier runs via `.github/workflows/deep.yml`
on a nightly schedule on `main`, on demand via `workflow_dispatch`, and as a reusable
workflow (`workflow_call`) for release gating. Platform legs are light: each of the five
platforms (Linux x64, Linux arm64, macOS arm64, macOS x64, Windows x64) publishes and
smoke-tests its native self-contained archive in a stripped environment without .NET on PATH.
Heavy work runs once on Linux (`ubuntu-latest`): full protocol unit tests, packed CLI tool
smoke test, parallel fuzzing across all targets for a long budget, Stryker mutation testing
across all Protocol source, and high-iteration property checks at 100,000 iterations.
All results aggregate into a machine-readable `deep-summary.json` artifact, whose top-level
`passed` field is the verdict.

Nightly, release (`workflow_call`) and default manual runs are strict: every fuzz target runs
for at least the strict floor (`STRICT_FUZZ_FLOOR_SECONDS`, 1800 s per target), and the summary
fails if any target or any of the budget is missing. Only a manual dispatch with `strict`
unticked may run a single target or a shorter budget. `strict` is not a `workflow_call` input,
and a run counts as non-strict only when the top-level workflow is `deep.yml` itself
(`github.workflow_ref`), so a calling workflow cannot make a run non-strict even if its own
inputs leak into the called workflow.

### How Diffra gates itself

Diffra self-evaluates every pull request against the quality budgets in
`baselines/dogfood/rules.json`, read from the pull request's own checkout, to prevent
unintended changes to metrics or policies.

- **Pinned trusted judge:** Diffra never allows a candidate pull request to act as
  its own judge. Instead, self-evaluation runs an immutable, released version of
  `Diffra.Cli` pinned in the local manifest `.config/dotnet-tools.json`. In CI, the
  runner downloads the package from GitHub Releases, validates its SHA-256 checksum
  against the committed digest in `.config/judge-sha256.json`, and restores the tool
  from an isolated `nuget.config` containing `<clear/>` so `nuget.org` cannot serve
  the package. RunDogfood executes `dotnet diffra ...` using the restored tool.
  Moving the judge to a newer release updates the tool manifest and the committed SHA-256.
- **Baseline storage and authority:** Quality baselines live on an orphan branch named
  `diffra-baselines`, stored as canonical Evidence documents at `evidence/<sha>.json`.
  Only post-merge pushes to `main` have `contents: write` permissions to record new
  baselines for verified commits. Pull request jobs run with read-only access
  (`contents: read`), fetch the Evidence for their merge-base commit, synthesize a
  Baseline reference that the script issues itself (nothing verifies it), and
  evaluate the Delta. If no baseline exists for the merge-base (such as in new repositories or bootstrap runs), the gate reports the
  assessment outcome as `inconclusive` in the step summary without failing the build.

### Releasing Diffra

`release.yml` runs on a `v*` tag push and on a manual `workflow_dispatch` dry run. Both
take the same path, on the exact commit being released (`github.sha`: the tagged commit,
or the head of the dispatched ref):

1. `preflight` resolves the version from `Directory.Build.props`. On a tag push it fails
   unless the tag is `v<version>`, `CHANGELOG.md` has a non-empty `## <version> ` section,
   and every earlier published release has a regression corpus with at least one case. A
   dry run (any dispatch, even on a tag ref) only warns about the notes and the corpora.
2. `deep` and `regression` call `deep.yml` and `regression.yml` as reusable workflows
   with `contents: read`. A called deep run is always strict, so a release cannot shorten
   the fuzz budget, skip a fuzz target or lower a mutation floor.
3. `build-and-package` needs both tiers. It packs the CLI tool, publishes the five
   self-contained archives, smoke-tests the linux-x64 archive with no .NET on `PATH`,
   and writes `SHA256SUMS`. It runs no tests of its own; the deep tier covers them.
4. `publish-github-release` runs only for a tag push, never for a dispatch (even on a tag
   ref). It needs the build and both tiers, and is the only job with `contents: write`.

If either tier fails or is skipped, no GitHub Release is created. A dry run that is green
and has no `preflight` warnings therefore means the commit is ready to release.

A release PR bumps `<Version>` and adds the `CHANGELOG.md` section. Once it merges, tag
the merge commit `v<version>`. The release is gated by the frozen corpora of earlier
releases, which the regression tier replays against the candidate build.

The release's own corpus, `tests/regression/<version>/`, is added afterwards by a separate
PR, generated from the package the GitHub Release published. That way the provenance in
the corpus (the package's SHA-256) is the digest of the published binary, not of a rebuild:

1. Download the published package and its checksums into an empty `temp/release/`, and
   check the package against them. `gh release download` will not overwrite files already
   there, and macOS `sha256sum` passes `--ignore-missing` with nothing checked, so the first
   line makes sure the package is present:

   ```sh
   gh release download v<version> -D temp/release -p 'Diffra.Cli.*.nupkg' -p SHA256SUMS
   test -f temp/release/Diffra.Cli.<version>.nupkg
   (cd temp/release && sha256sum -c --ignore-missing SHA256SUMS)
   ```

2. Install the tool from that package only. Use a NuGet config whose sources are just that
   folder, and an empty package cache, so no other build of the same version can be
   installed. Save this as `temp/release-nuget.config`:

   ```xml
   <configuration>
     <packageSources>
       <clear />
       <add key="release" value="release" />
     </packageSources>
   </configuration>
   ```

   ```sh
   NUGET_PACKAGES="$PWD/temp/release-packages" dotnet tool install Diffra.Cli \
     --configfile temp/release-nuget.config --tool-path temp/release-bin --version <version>
   ```

3. Run the one-command generator, passing the package digest from the published
   `SHA256SUMS`; see "Adding a Corpus for a New Release" in
   [tests/regression/README.md](../tests/regression/README.md):

   ```sh
   dotnet run --project tests/regression/Diffra.Regression.csproj -- generate <version> \
     --tool temp/release-bin/diffra \
     --package temp/release/Diffra.Cli.<version>.nupkg \
     --expected-sha256 <digest-from-SHA256SUMS>
   ```

4. Open a PR that commits `tests/regression/<version>/`. From then on, the corpus gates
   every later release. The next release's `preflight` fails while it is missing.

### Deep test strength as Evidence

The deep tier also records how strong the tests are, so that weaker tests count as a
regression even when every test still passes. The `test` job runs the suite once under
coverlet (`COVERLET_VERSION`), giving the test count and the line and branch coverage of
`Diffra.Protocol` from the same run. `scripts/DeepSummary.cs` adds the coverage and the
mutant counts to `deep-summary.json`. The final `deep` job then runs
`scripts/DeepEvidence.cs`:

1. **Payloads.** `payloads` turns the summary into four structured numeric payloads, one per
   facet, valid against [`baselines/deep/payload.schema.json`](../baselines/deep/payload.schema.json).
   The workflow checks them against that schema.
2. **Self-check.** `evidence` first proves, with the pinned judge, that the encodings below
   survive a real comparison. It takes this run's payloads as a synthetic baseline. It moves
   every metric by the smallest step its encoding allows, up and down, and applies a clear
   regression to each facet. It then collects, diffs and evaluates each case, and requires the
   expected outcome. Any judge error fails the step. The table is in the job summary.
3. **Collect.** It collects one Evidence document per facet with the pinned judge (type
   `diffra.metrics.deep-<facet>`, subject `diffra/deep`).
4. **Record or judge.**
   - **Record.** The nightly run of `deep.yml` on `main` records its Evidence and does not judge
     it. The separate `deep-baseline.yml` workflow then publishes it (see below).
   - **Judge.** Every other run looks for main deep Evidence. It walks first-parent history
     back from the run's commit (excluding the commit itself, up to 1000 commits) and takes
     the nearest ancestor that has Evidence on `diffra-baselines` at
     `evidence/deep/<sha>/<facet>.json`. It builds an `approved-main` Baseline Reference, then
     diffs and evaluates each facet with `baselines/deep/<facet>.rules.json`.
   - **No baseline.** If the branch or any ancestor's Evidence is missing, the verdict is
     `inconclusive`. The step summary says so and the run does not fail.

The verdict is the most severe facet outcome, in the evaluator's order: `fail`, `inconclusive`,
`needs-review`, `warn`, `pass`. It is written to the job summary with each facet's Markdown
report, and is a `deep.yml` output (`evidence-verdict`). The `deep` aggregate job fails on
`fail`, and when the Evidence cannot be built, collected or judged. Every other verdict is
reported without failing the run. `needs-review` means the set of metrics changed, for example
a new fuzz target or platform. The `deep-evidence` artifact holds the payloads, the Evidence,
each facet's Delta and Assessment, and a `manifest.json`.

The pinned evaluator applies one threshold set to every path in a Delta, and only an
increase is a regression. Every gated metric is therefore stored so that weaker is higher,
and metrics that need different thresholds are separate facets:

| Facet | Metric (path) | Encoding | Rule |
| --- | --- | --- | --- |
| strength | `/tests/negatedCount` | minus the number of passing tests | fail on any increase (`failAboveAbsoluteIncrease` `0`) |
| strength | `/coverage/uncovered{Line,Branch}BasisPoints` | uncovered lines and branches of `Diffra.Protocol`, in basis points | fail on any increase |
| strength | `/fuzz/findings` | total fuzz findings | fail on any increase, so a finding above a zero baseline fails |
| mutation | `/mutation/{protocol,comparison,assessment}SurvivedBasisPoints` | survived plus no-coverage mutants, in basis points of scored mutants (10000 minus the mutation score) | warn above +25 bp, fail above +100 bp |
| throughput | `/fuzz/<target>/nanosecondsPerExecution` | 10^9 / execs per second, three significant figures | warn above +25%; no fail threshold yet |
| size | `/archives/<platform>`, `/cliToolPackage` | bytes, three significant figures | warn above +5%, fail above +20% |

- **Mutation noise.** Mutation scores move between runs on an unchanged tree, because a
  mutant near the test timeout can flip between survived and timed out. Two runs of the same
  source gave 761 survived and 7 timed out (4149 bp) and 758 survived and 8 timed out
  (4139 bp) on `Diffra.Protocol`. Comparison and Assessment come from the same Stryker run and
  can flip the same way, though those runs had no timeouts there. They share the facet and its
  tolerance; with 212 and 55 scored mutants, one more surviving mutant is about 47 bp
  (Comparison, a warning) or 182 bp (Assessment, a failure). The test count and coverage were
  stable across those runs and keep zero tolerance in the strength facet.
- **Throughput noise.** At the 30 s per-target budget, two runs of the same target gave 17550
  and 35298 execs/s. Throughput therefore only warns. A fail threshold waits until nightly
  runs give enough data to set one.
- **Zero baselines.** Strength and mutation use absolute thresholds only, so a zero baseline
  (no findings) still compares. Throughput and size values are never zero.
- **Methodology.** Each methodology starts with `deep-v1`; a change of encoding bumps it. The
  throughput methodology also names the fuzz budget per target. A short ad hoc run is then
  incompatible with a nightly baseline and reports `inconclusive`, not a slowdown. Fuzz
  execution totals are budget times rate, so they stay in `deep-summary.json` and are not gated.

**Decimal-formatting bound.** The pinned judge (0.0.1-alpha.1) writes a Delta `relative` value
below 1e-4 in exponent form, and its own `eval` then rejects it as an invalid decimal string.
The encodings keep every real change at a relative change of at least 1e-4:

- Exact metrics (tests, findings, basis points) are integers within ±10000, so a change of
  one is at least 1e-4.
- Throughput and size are integers of at least 100 rounded to three significant figures, so
  their smallest step is about 1e-3. This also keeps run-to-run jitter of a few bytes out of
  the size gate.

The schema enforces the value ranges, and `DeepEvidence.cs` (`AssertJudgeSafe`) also enforces
the three-significant-figure rounding. The self-check exercises them at
the edges: 10000 to 10001, -10000 to -9999, and the step across a power of ten. It also runs an
informational control with a 1e-5 change, which this judge rejects. Once the judge pin moves to
a release with the decimal-formatting fix, the control is accepted and the job prints a
notice. The bounds, the `AssertJudgeSafe` check and the rounding can then be relaxed to finer
units, with a methodology bump.

**Publishing main baselines.** `deep.yml` keeps no job with write access, because it is a
reusable workflow meant for read-only callers, such as the release gate in `release.yml`. A run records only
when all of these hold:

- it is strict;
- the event is `schedule`;
- the ref is `refs/heads/main`;
- `github.workflow_ref` is this repository's own `deep.yml` on `main`.

A called run, a dispatch, a branch and a fork never record. When such a run succeeds,
`deep-baseline.yml` publishes it. It is triggered by `workflow_run` and runs only for a
successful scheduled Deep run on `main` of this repository. Its one job has `contents: write`
and `actions: read`. It:

1. downloads that run's `deep-evidence` artifact;
2. checks that the manifest is a record for exactly the run's head commit;
3. commits `evidence/deep/<sha>/{strength,mutation,throughput,size}.json` to `diffra-baselines`, with
   the same clone, orphan-bootstrap and retrying push as `ci.yml`'s `record-baseline-publish`.

The nightly run repeats on an unchanged `main` and fuzz throughput varies, so the first
Evidence recorded for a commit is kept and later runs leave it alone. Deep Evidence lives
under `evidence/deep/`, beside the dogfood Evidence at `evidence/<sha>.json`. The two never
collide.

**Verifying the main write.** After merge, the first nightly Deep run on `main` (or the next
one after any change to this flow) should show:

- a `recorded` verdict in its job summary;
- a successful `Deep baseline` run that commits `evidence/deep/<sha>/` to `diffra-baselines`
  (check with `git ls-tree origin/diffra-baselines evidence/deep/`).

The next deep run on a branch, a dispatch or a release then reports a real verdict against
that commit instead of `inconclusive`.

## Acceptance checks

- A private release with a narrowly scoped read token produces the same folder
  as a public fixture with the same bytes. No credential appears in that folder
  or the report HTML.
- A 1 GiB fixture streams within a bounded memory budget. An interrupted fetch
  either resumes with revalidation or restarts without accepting partial bytes.
- A wrong digest, duplicate path, escaping symlink, missing required asset, or
  oversized declaration fails without replacing a valid prior output folder.
- An offline browser can view a fetched image and download a binary through
  `--serve`. Only declared files under the prepared root are reachable.
- Every declared release executable runs on its target platform without an
  installed .NET runtime. The tool package installs and runs from a pinned
  local manifest.
- PR CI has no release write permission. A tag release is traceable to one
  source commit, and a post-upload fetch verifies every published asset.

## Source references

- [GitHub release asset API](https://docs.github.com/en/rest/releases/assets)
  documents asset metadata, digest, read permission, and binary download responses.
- [GitHub release limits](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)
  document the asset count and per-file size limits.
- [GitHub Actions artifact download and retention](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts)
  documents read access and default retention.
- [GitHub Actions permissions](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax)
  documents job-scoped `GITHUB_TOKEN` permissions.
- [GitHub Actions OIDC](https://docs.github.com/en/actions/reference/security/oidc)
  documents short-lived cloud identity claims.
- [.NET single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
  documents runtime-specific self-contained output.
- [.NET tool packaging](https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools-how-to-create)
  documents `PackAsTool` and command naming.
- [ASP.NET Core static files](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/static-files?view=aspnetcore-10.0)
  documents physical file providers and the serving root.
