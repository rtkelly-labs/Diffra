# Diffra documentation

Start with the [vision](vision.md) for the purpose and boundaries of Diffra, and
[Diffra in testing terms](testing-concepts.md) for how it relates to snapshot, approval and
differential testing. The [repository README](../README.md) has a runnable local workflow.

| Page | Use it for |
| --- | --- |
| [Diffra in testing terms](testing-concepts.md) | The oracle problem, the techniques Diffra builds on, and what it adds |
| [CLI reference](cli.md) | Every command and option, the metrics payload, the rules file, and exit codes |
| [Protocol v1](protocol.md) | Document shapes, result states, and CLI flow |
| [Trust and identity](trust-and-identity.md) | Hash rules, baseline roles, and promotion requirements |
| [Publication](publication.md) | The route into `docs.ryankelly.dev` and its build dependencies |
| [Artifact delivery and CI](artifact-delivery-and-ci.md) | Private and large artifact retrieval, local hosting, CLI packages, and release flow |
| [Parquet.SourceGenerator adopter](adopters/parquet-source-generator.md) | The proposed first integration |
| [Decision records](adr/) | Choices that constrain later implementations |

The original [handover vision](../VISION.md) and [plan](../PLAN.md) preserve the
project brief. The pages in this directory describe the repository as it stands
and the work still planned.

## Feature status

Each capability is in one of three states. **CLI** means a `diffra` command runs it. **Library**
means `Diffra.Protocol` implements it and the check suite exercises it, but no command or script
calls it yet; an adopter would call the .NET API directly. **Planned** means it is designed but not
built.

| Capability | State | Source | Decision |
| --- | --- | --- | --- |
| Canonical JSON and document identity | CLI | `CanonicalJson.cs`, `DocumentIdentity.cs`, `EvidenceIdentity.cs` | [ADR 0001](adr/0001-canonical-json-and-hash.md) |
| Numeric Evidence (`collect`) | CLI | `src/Cli/CollectCommand.cs` | |
| Numeric comparison (`diff`) | CLI | `Comparison.cs` | [ADR 0004](adr/0004-policy-status-vocabulary.md) |
| Increase budgets (`eval`) | CLI | `Assessment.cs` | [ADR 0004](adr/0004-policy-status-vocabulary.md) |
| HTML, JSON and Markdown reports (`report`) | CLI | `Reporting.cs` | |
| Content-addressed store and portable bundles (`bundle`) | CLI | `ContentAddressedStore.cs`, `EvidenceBundle.cs` | |
| Review bundle and local server (`present`) | CLI | `PresentationBundle.cs`, `src/Cli/PresentCommand.cs` | |
| Self-verification pipeline | CI script | `scripts/RunDogfood.cs` | |
| Git merge-base baseline resolver | Library | `GitBaselineResolver.cs` | [ADR 0003](adr/0003-baseline-trust-and-promotion.md) |
| Two-run verification and promotion records | Library | `Promotion.cs` | [ADR 0003](adr/0003-baseline-trust-and-promotion.md) |
| Keyed and ordered collection comparison | Library | `KeyedCollectionComparator.cs` | [ADR 0005](adr/0005-stable-collection-identity.md) |
| Cyclomatic complexity evidence | Library | `Complexity.cs`, `schemas/complexity-v1.schema.json` | |
| Code coverage evidence | Library | `Coverage.cs`, `schemas/coverage-v1.schema.json` | [ADR 0006](adr/0006-standardized-code-coverage-evidence.md) |
| Time-series projections and charts | Library | `TimeSeriesGraph.cs`, `schemas/timeseries-v1.schema.json` | [ADR 0007](adr/0007-time-series-projections-and-evolution-graphing.md) |
| CloudEvents envelope | Library | `CloudEvents.cs`, `schemas/cloudevent-diffra-v1.schema.json` | [ADR 0009](adr/0009-cloudevents-specification-and-event-driven-integration.md) |
| Composition graph: validation and execution | Library | `Graph.cs`, `GraphValidator.cs`, `GraphExecutor.cs` | [ADR 0002](adr/0002-explicit-registration-and-migration.md) |
| External command producers | Library | `ExternalCommandAdapter.cs`, `EvidenceDocumentBuilder.cs` | |
| Project file, typed metrics and collection root | Planned; schemas and examples only | `schemas/project-v1.schema.json`, `schemas/collection-v1.schema.json`, `examples/project-file/` | [ADR 0010](adr/0010-project-file-typed-metrics-and-collection-root.md) |
| `baseline verify`, `baseline update`, promotion commands | Planned | | [ADR 0003](adr/0003-baseline-trust-and-promotion.md) |
| Cross-version migrations | Planned | | [ADR 0002](adr/0002-explicit-registration-and-migration.md) |
| Parquet.SourceGenerator integration | Planned | | [adopter plan](adopters/parquet-source-generator.md) |
| Wide events and OpenTelemetry emission | Planned; CloudEvents already carry `traceparent` | | [ADR 0008](adr/0008-wide-events-otel-telemetry-and-intertwined-history.md) |

Source paths are under `src/Protocol/` unless shown otherwise.
