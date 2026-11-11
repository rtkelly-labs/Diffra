# Record entry examples

Each `.json` file here is one line of a job's entries file, as `diffra record` would write it
([Recording a run](../../docs/record.md)). Every file must validate against
[`schemas/record-entry-v1.schema.json`](../../schemas/record-entry-v1.schema.json).

| File | Shows |
| --- | --- |
| `value.json` | A `--value` with a unit. |
| `keyed-value.json` | A `--value` for one member of a keyed dataset, with a dimension. |
| `replaced.json` | A value recorded with `--replace`. |
| `from-file.json` | Typed data read from two files by the dataset's converter. |
| `skipped.json`, `failed.json` | A dataset not collected, and a collector that broke. |
| `rejected.json`, `rejected-value.json` | Entries kept with what was given and why it was rejected, including a project the root file does not declare. |

Every file in [`invalid/`](invalid/) must fail validation; its name says why. CI checks both sets.
