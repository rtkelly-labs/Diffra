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
type, byte length, and safe relative path. Evidence, Delta, Assessment, and
baseline approval retain their own identities. Materializing or rendering a
report does not change those identities.

Add a separate, versioned delivery index for transport. It maps each required
document or artifact digest to a source such as a GitHub Release asset ID, or a
future object-store key. It records the repository, immutable release ID and tag,
source commit, expected size, and digest. The delivery index receives its own
digest. A mutable `latest` selector may help a person discover a release, but the
resolved index and presentation inventory record exact IDs. CI requires an
explicit tag or index digest.

A release asset is a storage location, not proof that a baseline is trusted. The
baseline resolver and approval policy still decide trust. The CLI rejects a
delivery index that disagrees with the protocol manifest or the bytes it fetches.

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
    baseline-reference.json
    baseline-evidence.json
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

The host does not calculate trust, download files, or mutate documents during a
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
Evidence and artifacts, compares against a baseline resolved from a trusted
mainline, evaluates policy, and uploads a temporary review bundle. It uses
read-only repository permissions and cannot promote its own baseline. Required
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
| Deep | Nightly on `main`, on demand, and before a release (`deep.yml`) | Five-platform archive smoke matrix, full-scope Protocol mutation testing (floor 57 ratcheting upward, 80 gate on Comparison and Assessment), fuzzing across all targets, 100k property runs, and unit/e2e checks. Aggregated into `deep-summary.json`. Will be required for release (release gate). |
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

Diffra self-evaluates every pull request against trusted quality budgets to prevent
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
  verified Baseline Reference, and evaluate the Delta. If no baseline exists for the
  merge-base (such as in new repositories or bootstrap runs), the gate reports the
  assessment outcome as `inconclusive` in the step summary without failing the build.

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
