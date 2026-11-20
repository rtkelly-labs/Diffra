# 0010: Gate changes, release comparison and API versions

- Status: proposed
- Date: 2026-11-20

This record is a proposal. Nothing in it is accepted. It has three parts:

- **A.** Gate changes and locks.
- **B.** Comparison against a release.
- **C.** API changes and semantic versions.

B needs A's rules for baseline versions. C needs B's comparison point. Each can be accepted, changed
or refused on its own. The section [Where this comes from](#where-this-comes-from) separates the
owner's direction from the proposals made here.

## Context

**Gates move in the same pull request as the code they judge.**

- [#140](https://github.com/rtkelly-labs/Diffra/pull/140) added YamlDotNet to the CLI package.
  - In the same change it raised the self-evaluation's package growth budget from 50,000 to 200,000
    bytes.
  - [#142](https://github.com/rtkelly-labs/Diffra/pull/142) restored it.
  - The raise was deliberate. But nothing in Diffra's output separated "the package grew" from
    "the limit that would have caught the growth was raised".
- ADR 0002 decision 9 judges a pull request by its own copy of the root file, and ADR 0005
  decision 3 shows that the root file changed. Neither says whether the change let the pull
  request's own values through.
- The same pattern applies to files outside Diffra that act as gates:
  - `NoWarn` lists;
  - `#pragma warning disable`, `[SuppressMessage]` and `GlobalSuppressions.cs`;
  - .NET `CompatibilitySuppressions.xml` and `ILLink.Suppressions.xml`;
  - static-analysis baselines;
  - the version a package is checked against.
- In a repository built with `TreatWarningsAsErrors`, as Diffra is, the warning count is always
  zero. What moves is the number of suppressions.

**Package projects are compared with what they shipped.**

- A consumer upgrades from a published version, not from the commit a pull request branched from.
- A member removed and restored between two releases never reached anyone. A member removed since
  the last release breaks every consumer of it.
- The vision says "There is no comparison with the previous release for now".
- ADR 0003 decision 2 fixes one comparison point by ancestry.

A survey of 35 package-publishing projects (23 .NET; the rest Rust, JavaScript and TypeScript,
Python, Java and Go) found the following.

- **The release baseline is usually declared, not derived from git.**
  - [Polly](https://github.com/App-vNext/Polly/blob/main/eng/Library.targets) sets
    `PackageValidationBaselineVersion`, and a bot raises it after each release (to confirm).
  - The [Azure SDK](https://github.com/Azure/azure-sdk-for-net/blob/main/doc/dev/SDKBreakingChanges.md)
    sets `ApiCompatVersion`. Opting out is listed in a file under code owners (to confirm).
  - OpenTelemetry .NET and Newtonsoft.Json pin a version (to confirm).
  - dotnet/runtime pins a release candidate of the previous major (to confirm).
  - Some baselines lag the newest release on their line (to confirm per project).
- **Comparison against the merge base is the norm for bundle size and Go API checks.**
  - React's size bot and Kubernetes' `hack/apidiff.sh` do this (to confirm).
  - cargo-semver-checks compares with the newest version on crates.io.
- **Golden files are a ratchet with a separate release step.**
  - `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`, Angular's API goldens and
    PublicApiGenerator snapshots work this way.
  - New API goes into Unshipped. A release pull request moves it into Shipped.
- **Approvals happen at known points.**
  - dotnet/runtime requires an `api-approved` label before an API pull request.
  - Angular requires a public-API reviewer group.
  - These are the moments a person must judge a change.
- **The owner's own repositories have the same pattern.**
  - [Parquet.SourceGenerator](https://github.com/rtkelly-labs/Parquet.SourceGenerator), second in
    the [rollout](../../PLAN.md#rollout), publishes NuGet packages.
  - Its runtime package keeps `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`.
  - Its `Directory.Build.props` gives test, benchmark and tooling projects a `NoWarn` list of ten
    analyser rules.
  - Its release takes the version from the pushed tag.

## Decision (proposed)

### Part A: gate changes and locks

1. **A gate** is any of the following:
   - a rule, rule set, project mapping, `required` flag or lock in the root file;
   - a file the root file declares as a gate, under `gates`.
2. **A gate change is surfaced.** A pull request that changes a gate gets a finding above the
   others, with the status `needs-review`.
   - This extends ADR 0005 decision 3 from "the root file changed" to the changes that matter.
3. **A loosening is found by effect, not by reading rule text.**
   - The pull request's data is evaluated twice: once under its own copy of the root file and once
     under the copy at its comparison point.
   - When the comparison point's copy gives a more severe status for any finding, the pull request
     loosened a gate that it needed. That gives the finding "this pull request changed the gate it
     would otherwise fail", with the status `needs-review` (`review`). On a locked rule the more
     severe status stands (decision 5): the pull request takes the earlier rule's status, which
     is `act` when that rule was a `fail` rule.
   - This handles a raised threshold, a deleted rule, a dataset made optional and a rule set
     unassigned without listing them.
   - A dataset that is new in the pull request has no earlier rule and is judged only by the new
     copy.
4. **Diffra's default: changes to gates go in their own pull requests.** A pull request that
   changes a gate and also changes anything else gets a "mixed gate change" finding.
   - The finding is `needs-review`, so the attention level is `review`.
   - It is a finding like any other. It fails the step only under `fail-on`
     ([0005](0005-surface-do-not-enforce.md)).
5. **A lock makes the separate pull request take effect.** `lock: true` in the root file applies at
   any of the levels in decision 6. A locked rule is judged by the more severe of the two evaluations
   in decision 3.
   - A pull request can tighten a locked rule and have it apply at once.
   - A loosening applies only after it has merged in a pull request of its own.
   - This is modelled on Pulumi's `protect` option. Pulumi will not delete a protected resource; a
     first update must remove the protection.
6. **Locks cascade and can be overridden on a single rule.** The levels, from least to most
   specific, are the file, a dataset, a rule set and a single rule. The most specific setting wins.
   - `lock: false` on one rule exempts it under a locked file, for example a noisy timing.
   - `lock: true` on one rule locks only that rule.
7. **Unlocking is a loosening.** A rule is locked when either copy locks it. Removing a lock or
   adding `lock: false` therefore applies only after that change merges alone. Unlocking and
   loosening take two pull requests.
8. **Gate files outside Diffra** are listed under `gates` with a lock setting.
   - Diffra cannot evaluate another tool's file twice. For these files, a mixed change is `act`
     when the file is locked and `review` when it is not.
   - "Alone" means the pull request changes only gates. A release pull request that moves lines
     from `PublicAPI.Unshipped.txt` to `PublicAPI.Shipped.txt` changes only those files, so it is
     not mixed.
   - Who may edit any gate stays with code owners and branch protection (ADR 0005 decision 7).
9. **Ratchets use the existing rule shapes.** No baseline document is stored or promoted (ADR 0003
   decision 1).
   - "Must not rise" is `versus: { fail: { increase: { above: 0 } } }` on a deterministic count.
     The comparison point is the record of the commit the pull request branched from, so a
     merged improvement becomes the next pull request's reference.
   - A ceiling is `check`.
   - New and removed keyed entries, such as individual warnings, are judged by `keys`.
10. **Headroom is reported.** When a value is below its `check` ceiling, the full report gives the
    tighter value, for example "suppressions 3, ceiling 10".
    - The line never appears in the comment and never fails.
    - Diffra does not edit the root file.
11. **Suppressions are a dataset.**
    - The count is taken by rule ID from the compiler's SARIF log. Roslyn records suppressed
      diagnostics there (to confirm).
    - Or from a scan of the tree for the forms listed in the context.
    - Either way it is ratcheted like a warning count.

### Part B: comparison against a release

12. **A release comparison is a rule mode, not a second comparison point.**
    - A rule set may hold `release` beside `check`, `versus`, `series` and `keys`. `release` has
      the shape of `versus`.
    - It is judged against the record of the release commit.
    - Rules without it keep the one comparison point of ADR 0003 decision 2.
    - This amends the vision's "no comparison with the previous release".
13. **The release commit is resolved by one of three resolvers**, set per project:
    - **`declared`** reads a version the build already declares, such as the MSBuild property
      `PackageValidationBaselineVersion`.
      - This is the default for .NET, where the survey found baselines are mostly declared.
      - Changing the property is a gate change (decision 1).
    - **`tag`** takes the nearest ancestor commit with a tag matching a pattern.
      - A monorepo's per-package prefix, such as `Pkg_` or `pkg@`, goes in the pattern.
      - This keeps to ancestry. It fails on the main branch after a release branch is cut, where
        the release tag is not an ancestor.
    - **`registry`** takes the newest version on the package registry and maps it to its tag.
      cargo-semver-checks uses the same default.
14. **Release records outlive the history window.**
    - The record of every resolved release commit is kept beyond the window that bounds the
      ordinary comparison point (ADR 0003 decision 4).
    - Where it is kept is open: pinned in the history store, or attached to the release.
15. **Baseline lag is reported.** When a `declared` baseline is older than the newest release tag
    on the same line, the full report says so.
16. **Noisy measures are not judged against an old record.**
    - Measures such as benchmarks are compared with a release only through a paired measurement:
      the job runs the released package and the head in the same job and reports the ratio, which
      Diffra records as a table.
    - BenchmarkDotNet can compare against a package version this way.
17. **The comment shows both points when both apply**, for example "+1 this pull request, +14
    since v2.3.0".

### Part C: API changes and semantic versions

18. **Every surfaced change to a stable public API needs a person's judgement.**
    - Such a change is at least `review`.
    - Diffra proposes how large a version change it needs and says why. A person decides.
19. **Each API element change carries four fields**, set by the converter from the ecosystem's own
    rules:
    - **`kind`**: added, removed, signature changed, visibility changed, modifier changed,
      nullability changed, constraint changed or unclassified.
    - **`compat`**: whether the change breaks `binary`, `source` or `behaviour` compatibility.
    - **`stability`**: `stable`, `preview` or `internal`, from the ecosystem's markers (table
      below).
    - **`bump`**: the default required version increment, one of `major`, `minor`, `patch` or
      `none`.

    Elements are keyed by a stable identity. For .NET this is the documentation-comment ID, such
    as `M:Ns.Type.Method(System.Int32)`.
20. **Default bumps** for `stable` elements, with the ecosystem exceptions known so far (all to
    confirm per converter):

    | Change | Default | Exceptions |
    | --- | --- | --- |
    | Removed; signature changed; visibility reduced; constraint tightened | major | |
    | Abstract or interface member added | major | Minor with a default implementation (.NET, Java, a Rust trait method with a default body) |
    | Enum member or field added | minor | Major for an exhaustive Rust enum or struct |
    | Input type widened, or output type narrowed | minor | |
    | Output type widened, or input type narrowed | major | |
    | Nullability annotation changed | minor | Major in TypeScript under `strictNullChecks` (to confirm) |
    | Element added; marked deprecated | minor | |
    | Unclassified | none proposed | Always `review`, never assumed safe, as [#158](https://github.com/rtkelly-labs/Diffra/issues/158) proposes |

    - A change to a `preview` or `internal` element is `none`.
    - Where the tool already decides the version increment, Diffra records the tool's answer and
      does not apply this table.
21. **The version policy is set per project:**
    - **`semver`** (default).
    - **`semver-zero`**: in 0.y.z the minor number acts as major, as Cargo and npm's caret range
      treat it.
    - **`go-module`**: a major increment from v2 means a new module path.
    - **`none`**: changes are reported but no version increment is computed. This suits calendar
      versions and deprecation-cycle policies.
22. **A person's judgement is recorded in files, not in an approval step** (ADR 0005 decision 6).
    - **The declared version** is read from the version the project intends to ship:
      - changeset files, which already declare an increment per package;
      - MinVer, GitVersion or Nerdbank.GitVersioning;
      - `<Version>`, `Cargo.toml`, `package.json` or `pyproject.toml`;
      - the Go module path.
    - It is compared with the largest required increment since the release (Part B).
      - Declaring less than is required is `act`.
      - Declaring more is a line in the full report.
    - **A single change is reclassified in an override file** keyed by element identity. Where the
      ecosystem has such a file, Diffra reads it: `CompatibilitySuppressions.xml`, revapi's ignore
      configuration, or lint levels for cargo-semver-checks.
      - An override file is a gate (decision 1). Adding an override is surfaced, and needs its own
        pull request when locked.
23. **Converters read each tool's own output** (ADR 0002 decision 5). An optional extractor that
    reads a built .NET assembly with `System.Reflection.Metadata`, which needs no package
    reference, would be a separate program outside the core, as ADR 0007 decision 5 allows.

    | Ecosystem | Source | Stability markers | Decides the increment itself |
    | --- | --- | --- | --- |
    | .NET | Package validation and ApiCompat; `PublicAPI.*.txt`; PublicApiGenerator snapshots | `[Experimental]`, `[EditorBrowsable(Never)]` | No |
    | Rust | cargo-semver-checks; cargo-public-api | `#[doc(hidden)]`, unstable features | Yes (cargo-semver-checks) |
    | JavaScript and TypeScript | api-extractor `.api.json` | `@public`, `@beta`, `@alpha`, `@internal` | No |
    | Java and Kotlin | japicmp; revapi; Kotlin binary-compatibility-validator `.api` files | `@RequiresOptIn` | Yes (japicmp, revapi; to confirm) |
    | Go | apidiff; gorelease | `internal/`, `x/` modules | Yes (gorelease suggests a version) |
    | Python | griffe `check`; mypy stubtest | leading `_`, `__all__` | No |

## Examples

These examples use syntax that does not exist yet. They stay in this record, not in
`examples/root-file/`, until the schema accepts them, because the protocol checks load every file
in that folder.

### A NuGet library

A generic library with PublicAPI files, package validation against a declared baseline, and
analyser suppressions. Parquet.SourceGenerator would differ in one line: its version comes from the
pushed tag, so it would use `release: { resolve: tag, pattern: "v*" }`.

```yaml
schema: https://diffra.net/schemas/root-file-v1.schema.json
lock: true                       # Part A: every rule in this file is locked by default

gates:                           # files outside Diffra that act as gates (decision 8)
  - { path: "**/CompatibilitySuppressions.xml", lock: true }
  - { path: Directory.Build.props, lock: false }   # holds NoWarn, but also unrelated properties

datasets:
  suppressions:
    type: diffra/findings@1
    from: { sarif: "artifacts/build/*.sarif" }   # suppressed results only; how is open
    rules:
      default:
        fields:
          total:
            versus:
              fail: { increase: { above: 0 } }   # ratchet: suppressions never rise
          results:
            keys: { added: needs-review, removed: pass }

  api:
    type: diffra/contract@1            # #158; whether API gets its own type is open
    from: { publicapi: "src/*/PublicAPI.*.txt" }
    rules:
      default:
        keys: { added: needs-review, removed: pass }   # per pull request: new API is seen by a person
        release:                       # Part B: judged against the last release
          fail: { bump: { above: declared } }   # Part C: required increment exceeds the declared one

  packageSize:
    type: diffra/size@1
    from: { files: "artifacts/packages/*.nupkg" }
    rules:
      default:
        fields:
          total:
            versus:                    # the values in baselines/dogfood/rules.json
              warn: { increase: { above: 20000 } }
              fail: { increase: { above: 50000 } }
            release:
              needs-review: { increase: { above: 10% } }
              lock: true               # a single rule locked again (decision 6)
        lock: false                    # this rule set is exempt from the file lock

projects:
  library:
    release: { resolve: declared, property: PackageValidationBaselineVersion }
    version: { policy: semver, from: minver }
    datasets: { suppressions: default, api: default, packageSize: default }
```

### What a reader sees

| Pull request | Attention | First line of the comment |
| --- | --- | --- |
| Adds one public method; Unshipped grows; version 1.4.0 | `review` | API: 1 member added since v1.3.0 (minor). Declared 1.4.0 covers it. |
| Removes a public method shipped in v1.3.0; version 1.4.0 | `act` | API: `M:Lib.Reader.Open(System.String)` removed since v1.3.0 needs a major version; 1.4.0 is declared. |
| Removes a method added after v1.3.0 and never released | `quiet` | (no comment: `keys` passes a removal, and the method never reached a release) |
| Adds `CA1062` to `NoWarn` and changes code in the same pull request | `review` | Gate: `Directory.Build.props` changed together with code; suppressions +1 (if the SARIF log records it; to confirm). |
| Raises the size budget alone, nothing else changed | `review` | Gate: rule set `packageSize.default` changed. |
| Raises the size budget and adds a dependency that needs it | `review` | Gate: this pull request changed the gate it would otherwise fail (`packageSize` +150 KB). |

The last row is #140. If `packageSize` were locked, the same pull request would take the earlier
budget's `fail` status and be `act`, and the raise would take effect only after it merged alone.

### An npm package

A generic TypeScript package that uses changesets and api-extractor. mermaid-toolkit, third in the
rollout, uses neither today.

```yaml
datasets:
  api:
    type: diffra/contract@1
    from: { api-extractor: "temp/*.api.json" }
    rules:
      default:
        release:
          fail: { bump: { above: declared } }
projects:
  package:
    release: { resolve: tag, pattern: "v*" }
    version: { policy: semver-zero, from: changesets }
    datasets: { api: default }
```

An `@beta` member removed is `none` and stays quiet. A `@public` member removed at version 0.3.0
requires 0.4.0 under `semver-zero`.

## Diffra uses Diffra

Diffra publishes one package, the `Diffra.Cli` tool. Every other project sets
`IsPackable` to false. Its public surface is therefore not a library API. It is:

- the CLI's commands, options and exit codes;
- the JSON schemas in `schemas/`;
- the record formats.

Its one release is `v0.0.1-alpha.1`. Until an adopter has run these capabilities, they run in the
lane that never fails the build (vision, "Diffra uses Diffra"):

1. **Gates.**
   - When Diffra has its own root file, it is locked file-wide.
   - Until then, the self-evaluation script reports a change to `baselines/dogfood/rules.json`
     together with other files as a mixed gate change. Decision 3's check is run against the
     #140 and #142 history as a fixture.
2. **Suppressions.**
   - The count is zero today: no `NoWarn` in `Directory.Build.props`, and no
     `#pragma warning disable` or `[SuppressMessage]` in `src/`.
   - It is ratcheted at zero.
3. **Schema surface.**
   - `schemas/*.json` are compared with `v0.0.1-alpha.1` under `semver-zero`.
   - A removed property or a narrowed enum is a breaking change of the published contract.
   - Each schema's identifier carries a major version (`-v1`, ADR 0004 decision 8), so a breaking
     change also needs a new schema identifier.
4. **Package size since the release.** The existing size dataset gains a `release` rule against
   `v0.0.1-alpha.1`.

Parquet.SourceGenerator, second in the rollout, is the first adopter for Part C, because it
already keeps PublicAPI files. Its review triggers in the plan apply unchanged.

## Changes to accepted records

| Record | Change if accepted |
| --- | --- |
| [0002](0002-project-file-and-datasets.md) decision 9 | A pull request is judged by its own copy of the root file, except locked rules, which take the more severe of the two copies. |
| [0003](0003-comparison-history-and-noise.md) decisions 2 and 4 | The `release` rule mode has its own resolved point. Release records outlive the window. |
| [0005](0005-surface-do-not-enforce.md) decision 3 | Gate changes, loosenings and mixed gate changes are surfaced as findings. |
| [vision](../vision.md) | "There is no comparison with the previous release for now" is replaced. |

This record does not change ADR 0005 decisions 1, 2, 6 or 7. Diffra still surfaces, and blocks
only under `fail-on`. There is still no approval step, and code owners decide who edits gates.

## Where this comes from

- **The owner's direction, 2026-11-20:**
  - Diffra, not the adopter, chooses how gates may move.
  - Changes to Diffra's inputs belong in their own pull requests.
  - A strict option in the root file works like a Pulumi lock, and can be overridden per check.
  - Every surfaced API change needs a person to judge whether it is breaking, and whether it is
    major, minor or patch, with reasonable defaults per kind of change.
  - The design covers ecosystems other than .NET.
  - It includes examples and is used in Diffra's own evaluation.
- **Proposed here, not ruled:**
  - judging locked rules by the more severe of two evaluations;
  - the cascade of lock levels;
  - the `review` and `act` levels in Part A;
  - the three resolvers and the `declared` default;
  - the rule mode's name and shape;
  - the default bump table;
  - the version policies;
  - all syntax.

## Not yet decided

- Whether the default in decision 4 is `review`, or quiet unless something is locked.
- Whether a loosening on an unlocked rule (decision 3) should be `act` rather than `review`.
- The name `release` for the rule mode, and whether it is a mode or a `versus` option.
- Where release records are kept beyond the window.
- Whether API surface uses `diffra/contract@1` or its own type.
  - A version value needs a type that holds a version string, which no dataset type has.
  - `diffra record --value` takes only numbers.
- How a `declared` resolver reads an MSBuild property without running MSBuild.
- Whether Diffra ships the assembly extractor in decision 23.
- Whether `gates` takes globs, and how a renamed gate file is followed.
- What "more severe" means when the two copies select different rule sets for a dataset.
- Which history shapes the resolvers are tested against. ADR 0003 decision 8 tests five. Part B
  adds at least three: a main branch after a release branch is cut, a hotfix branch made from a
  tag, and a monorepo with tag prefixes per package. GitFlow, with releases tagged on `main` and
  work on `develop`, is the fourth.

## Current code

None of this is built.

- The root file has no `lock`, `gates`, `release` or `version` keys.
- No converter reads API output.
- `GitBaselineResolver` resolves only the merge base.
- The self-evaluation compares the package size with the merge base under
  `baselines/dogfood/rules.json`.
