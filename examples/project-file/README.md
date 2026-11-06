# Project file examples

Worked examples for [ADR 0010](../../docs/adr/0010-project-file-typed-metrics-and-collection-root.md):
`diffra.project.yml`, typed metrics and the `diffra.collection.v1` root. Nothing here is implemented yet.
These files fix the contract before the loader, the converters and `eval` support are built.

## Project files

| Example | Shows |
| --- | --- |
| [`benchmarks/`](benchmarks/) | The Parquet.SourceGenerator benchmark gate (PR #752) as one `diffra/benchmark@1` metric: per-field budgets, an absolute `and` floor, per-field stratification by CPU, and a series window |
| [`coverage/`](coverage/) | `diffra/coverage@1` from Cobertura, with ADR 0006's floor and drop budget as `check` and `versus` rules |
| [`test-results/`](test-results/) | `diffra/test-results@1` from TRX and JUnit: tag transitions plus a duration rule that applies only when both sides succeeded |
| [`custom/`](custom/) | The escape hatch: a custom `variant` type and custom leaf metrics (`count`, `enum`, `digest`) read from a project's own payload |

`benchmarks/` and `test-results/` also carry a valid payload and the payload schema an emitter derives
from the project file (ADR 0010, section 13). These schemas are written by hand; nothing generates them
yet. `benchmarks/` carries two collection roots: one `complete`, and one honestly `incomplete`, which is
schema-valid but which `eval` must refuse (`inconclusive`).

Digests in the fixtures are placeholders with repeated digits. They are shaped like identities, but
nobody computed them.

## Invalid fixtures

Every file in [`invalid/`](invalid/) must fail validation against the schema named here.

| Fixture | Schema | Why it fails |
| --- | --- | --- |
| `project-rule-not-allowed-age-on-bytes.yml` | `project-v1` | `age` is a datetime rule, not a `bytes` rule |
| `project-rule-not-allowed-relative-ratio.yml` | `project-v1` | A ratio takes absolute thresholds only |
| `project-rule-not-allowed-variant-increase.yml` | `project-v1` | A variant tag is unordered; `increase` is not a tag rule |
| `project-rule-not-allowed-series-fail.yml` | `project-v1` | Series rules may not `fail` in v1 |
| `project-unit-on-fixed-unit-type.yml` | `project-v1` | `bytes` has the fixed unit `B` |
| `project-unknown-standard-field.yml` | `project-v1` | `diffra/benchmark@1` has no `gen3PerKOps` field |
| `benchmarks-payload-undeclared-metric.json` | `benchmarks/payload.schema.json` | Emits `allocations`, which the project never declared |
| `benchmarks-payload-wrong-unit.json` | `benchmarks/payload.schema.json` | Allocations in `bytes`, not `B` (the #752 prototype's spelling) |
| `benchmarks-payload-wrong-time-unit.json` | `benchmarks/payload.schema.json` | One mean time in `us`, not `ns` |
| `test-results-payload-field-outside-tag.json` | `test-results/payload.schema.json` | A `success` result carries `message`, which only `error` has |
| `collection-incomplete-claims-complete.json` | `collection-v1` | Claims `complete` over `failed` entries |
| `collection-zero-metrics.json` | `collection-v1` | Lists no metrics: an empty collection is an error, not a pass |
| `collection-nothing-collected.json` | `collection-v1` | Lists metrics, but none was collected |

## Validating

The repository has no JSON Schema validator in its .NET test suite yet, so CI does not run these checks.
To run them locally with [check-jsonschema](https://github.com/python-jsonschema/check-jsonschema), from
the repository root:

```sh
E=examples/project-file
uvx check-jsonschema --check-metaschema schemas/project-v1.schema.json schemas/collection-v1.schema.json \
  $E/benchmarks/payload.schema.json $E/test-results/payload.schema.json
uvx check-jsonschema --schemafile schemas/project-v1.schema.json $E/*/diffra.project.yml
uvx check-jsonschema --schemafile schemas/collection-v1.schema.json $E/benchmarks/collection*.json
uvx check-jsonschema --schemafile $E/benchmarks/payload.schema.json $E/benchmarks/payload.json
uvx check-jsonschema --schemafile $E/test-results/payload.schema.json $E/test-results/payload.json

# Each of these must report a validation error.
for f in $E/invalid/project-*.yml; do uvx check-jsonschema --schemafile schemas/project-v1.schema.json "$f"; done
for f in $E/invalid/collection-*.json; do uvx check-jsonschema --schemafile schemas/collection-v1.schema.json "$f"; done
for f in $E/invalid/benchmarks-payload-*.json; do uvx check-jsonschema --schemafile $E/benchmarks/payload.schema.json "$f"; done
uvx check-jsonschema --schemafile $E/test-results/payload.schema.json $E/invalid/test-results-payload-field-outside-tag.json
```
