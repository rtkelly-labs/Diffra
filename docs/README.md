# Diffra documentation

Start with the [vision](vision.md) for the purpose and boundaries of Diffra, and
[Diffra in testing terms](testing-concepts.md) for how it relates to snapshot, approval and
differential testing. The [repository README](../README.md) has a runnable local workflow.

| Page | Use it for |
| --- | --- |
| [Diffra in testing terms](testing-concepts.md) | The oracle problem, the techniques Diffra builds on, and what it adds |
| [CLI reference](cli.md) | Every command and option, the metrics payload, the rules file, and exit codes |
| [Recording a run](record.md) | The `diffra record` command, the entries file each job writes, and how the collecting job merges them |
| [Protocol v1](protocol.md) | Document shapes, result states, and CLI flow |
| [Trust and identity](trust-and-identity.md) | The hash rules, baseline roles and promotion requirements the current code implements |
| [Publication](publication.md) | The route into `docs.ryankelly.dev` and its build dependencies |
| [Artifact delivery and CI](artifact-delivery-and-ci.md) | Private and large artifact retrieval, local hosting, CLI packages, and release flow |
| [Parquet.SourceGenerator adopter](adopters/parquet-source-generator.md) | An earlier integration plan; the repository is now second in the rollout |
| [Glossary](glossary.md) | The words Diffra's documents, settings and output use |
| [Decision records](adr/) | The design Diffra is moving to, and how far the code is from it |

The [plan](../PLAN.md) gives the order of work. The original
[handover vision](../VISION.md) preserves the first project brief. The pages in
this directory describe the repository as it stands and the work still planned.

## Feature status

Each capability is in one of three states. **CLI** means a `diffra` command runs it. **Library**
means `Diffra.Protocol` implements it and the check suite exercises it, but no command or script
calls it yet; an adopter would call the .NET API directly. **Planned** means it is designed but not
built.

| Capability | State | Source | Decision |
| --- | --- | --- | --- |
| Canonical JSON and document identity | CLI | `CanonicalJson.cs`, `DocumentIdentity.cs`, `EvidenceIdentity.cs` | [0004](adr/0004-records-integrity-and-storage.md) |
| Numeric Evidence (`collect`) | CLI | `src/Cli/CollectCommand.cs` | |
| Numeric comparison (`diff`) | CLI | `Comparison.cs` | [0003](adr/0003-comparison-history-and-noise.md) |
| Increase budgets (`eval`) | CLI | `Assessment.cs` | [0006](adr/0006-pr-output-and-attention-levels.md) |
| HTML, JSON and Markdown reports (`report`) | CLI | `Reporting.cs` | |
| Attention levels, pull request comment and agent Markdown (`report --format comment\|agent`) | CLI | `Attention.cs` | [0006](adr/0006-pr-output-and-attention-levels.md) |
| Content-addressed store and portable bundles (`bundle`) | CLI | `ContentAddressedStore.cs`, `EvidenceBundle.cs` | |
| Review bundle and local server (`present`) | CLI | `PresentationBundle.cs`, `src/Cli/PresentCommand.cs` | |
| Self-verification pipeline | CI script | `scripts/RunDogfood.cs` | |
| Enforcement setting `fail-on` | Library; `merge` calls it | `Enforcement.cs`, `src/Cli/MergeCommand.cs` | [0005](adr/0005-surface-do-not-enforce.md) |
| Git merge-base baseline resolver | Library | `GitBaselineResolver.cs` | [0003](adr/0003-comparison-history-and-noise.md) |
| Two-run verification and promotion records | Library | `Promotion.cs` | [0005](adr/0005-surface-do-not-enforce.md) |
| Keyed and ordered collection comparison | Library | `KeyedCollectionComparator.cs` | [0003](adr/0003-comparison-history-and-noise.md) |
| Cyclomatic complexity evidence | Library | `Complexity.cs`, `schemas/complexity-v1.schema.json` | |
| Code coverage evidence | Library | `Coverage.cs`, `schemas/coverage-v1.schema.json` | [0002](adr/0002-project-file-and-datasets.md) |
| Time-series projections and charts | Library | `TimeSeriesGraph.cs`, `schemas/timeseries-v1.schema.json` | [0003](adr/0003-comparison-history-and-noise.md) |
| CloudEvents envelope | Library | `CloudEvents.cs`, `schemas/cloudevent-diffra-v1.schema.json` | [0008](adr/0008-optional-events-and-telemetry.md) |
| Graph data model and validation, to be reworked for the CI job graph | Library, in the separate `Diffra.Graph` project | `src/Graph/` | [0009](adr/0009-dependencies-and-extensions.md) |
| External command producers | Library | `ExternalCommandAdapter.cs`, `EvidenceDocumentBuilder.cs` | |
| Root file: settings, datasets, named rule sets and projects | Schema, examples and a loader that checks cross-references; `diffra record` and `diffra merge` call the loader | `schemas/root-file-v1.schema.json`, `examples/root-file/`, `src/RootFile/` | [0002](adr/0002-project-file-and-datasets.md) |
| `diffra record` and its entries file | CLI for `--value`, `--skipped` and `--failed`; no converter, so `from` cannot be read yet. Library code that validates entries, writes and reads entries files, and merges them; `diffra merge` for a collecting job, with no partial-record format or transport yet | [`docs/record.md`](record.md), `schemas/record-entry-v1.schema.json`, `examples/record/`, `RecordCommand.cs`, `MergeCommand.cs`, `Recording.cs` | [0002](adr/0002-project-file-and-datasets.md) |
| Earlier project file, typed metrics and collection root | Project file superseded by the root file; collection root kept, with no replacement yet | `schemas/project-v1.schema.json`, `schemas/collection-v1.schema.json`, `examples/project-file/` | [0002](adr/0002-project-file-and-datasets.md) |
| `baseline verify`, `baseline update`, promotion commands | Not planned: approval and promotion are not part of the open tool | | [0005](adr/0005-surface-do-not-enforce.md) |
| Cross-version migrations | Not planned during alpha: unreadable records are skipped and counted | | [0004](adr/0004-records-integrity-and-storage.md) |
| Parquet.SourceGenerator integration | Planned | | [adopter plan](adopters/parquet-source-generator.md) |
| Event and OpenTelemetry output | Planned as opt-in JSON-lines files; CloudEvents already carry `traceparent` | | [0008](adr/0008-optional-events-and-telemetry.md) |

Source paths are under `src/Protocol/` unless shown otherwise.
