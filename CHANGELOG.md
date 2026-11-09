# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## 0.0.1 - 2026-11-09

Diffra 0.0.1 is an initial unstable preview release. The protocol contracts, schemas, document shapes, and CLI flags may change in any 0.0.x release.

In this release, only the `Diffra.Cli` .NET tool package and standalone single-file executables are published. The protocol library (`Diffra.Protocol`) is bundled inside the tool package.

### CLI Commands

- `diffra collect`: Turn a numeric metrics payload into a canonical Evidence document recording environment, producer, and commit provenance.
- `diffra diff`: Compare candidate Evidence against baseline Evidence and a Baseline Reference to produce a policy-free Delta document.
- `diffra eval`: Evaluate Delta changes against policy rules and increase budgets to produce an Assessment verdict (`pass`, `warn`, `fail`, `needs-review`, or `inconclusive`).
- `diffra report`: Render standalone review reports in HTML, JSON, or Markdown formats.
- `diffra bundle`: Move evidence through a content-addressed store (CAS) with `bundle export` and `bundle import` zip archives.
- `diffra present`: Materialize self-contained review presentation bundles (`present bundle`) and host local reviews on loopback using ASP.NET Core (`present serve`).

### Library Capabilities (`Diffra.Protocol`)

The core protocol library provides the following capabilities (callable directly via the .NET API):

- **Canonical JSON & Identity**: RFC 8785 (JCS) deterministic subset and SHA-256 document hashing (`CanonicalJson.cs`, `DocumentIdentity.cs`, `EvidenceIdentity.cs`).
- **Baseline Resolution & Trust**: Git merge-base baseline resolver (`GitBaselineResolver.cs`).
- **Promotion & Verification**: Two-run promotion and verification approval records (`Promotion.cs`).
- **Collection Comparison**: Keyed and ordered collection comparison (`KeyedCollectionComparator.cs`).
- **Complexity Evidence**: Cyclomatic complexity evidence models and schemas (`Complexity.cs`).
- **Code Coverage Evidence**: Standardized code coverage evidence models and schemas (`Coverage.cs`).
- **Time-Series & Charting**: Time-series point projections and SVG chart generation (`TimeSeriesGraph.cs`).
- **CloudEvents**: CloudEvents envelope serialization and parsing (`CloudEvents.cs`).
- **Composition Graph**: DAG pipeline node validation and execution (`Graph.cs`, `GraphValidator.cs`, `GraphExecutor.cs`).
- **External Command Adapters**: Subprocess execution adapters and document building (`ExternalCommandAdapter.cs`, `EvidenceDocumentBuilder.cs`).

### Distribution

- `Diffra.Cli` .NET tool package (`Diffra.Cli.0.0.1.nupkg`) attached to the GitHub Release (publication to NuGet.org will come in a later release).
- Published self-contained single-file binary archives for `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, and `win-x64` with SHA-256 checksums (`SHA256SUMS`).
