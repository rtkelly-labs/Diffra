# Root file examples

Worked examples of the root file described in
[decision record 0002](../../docs/adr/0002-project-file-and-datasets.md), validated against
[`schemas/root-file-v1.schema.json`](../../schemas/root-file-v1.schema.json). Nothing loads the
root file yet. The file's name in a repository is not decided, so these examples are named by
what they show.

The rule shapes (`check`, `versus`, `series`, `keys`, `when`) and the dataset types are those of
the earlier [project file](../project-file/), copied into the root schema. What changes is where
rules sit and what a project is:

- **Datasets** replace metrics. A dataset is collected and stored once and has one history.
  `required: false` marks one as optional.
- **Rule sets** sit on the dataset under `rules`, each with a name. A standard dataset keeps its
  data settings, such as `stratify`, under `fields` and its rules under
  `rules.<name>.fields`. A custom collection keeps its kind and key normalization on the dataset
  and its severities for added, removed or changed entries under `rules.<name>.collection`.
- **Projects** select datasets and map each one to the name of the rule set it applies, or to
  `null` to record and show a dataset without applying rules. `scope` is gone.
- **Settings** hold the one-screen output limits (25 lines, 5 findings, 4 columns, about 500
  tokens for the agent-facing Markdown), the size of the history window and the size limits.
  The output numbers are the defaults. The history window and the size limits have no default
  in the schema yet.

The schema cannot check that a project names a rule set the dataset defines, or a dataset the
file declares. The loader will.

## Examples

| Example | Shows |
| --- | --- |
| [`release-train.yml`](release-train.yml) | Two projects over shared datasets: pull requests into main collect tests and coverage, releases add end-to-end results and judge the same data more strictly. An optional Windows dataset. |
| [`custom.yml`](custom.yml) | The earlier custom example rewritten for the root file: a custom variant type and custom leaf datasets read from the repository's own payload, collection rules in a rule set, and a dataset selected without rules. |

## Invalid fixtures

Every file in [`invalid/`](invalid/) must fail validation against `root-file-v1`.

| Fixture | Why it fails |
| --- | --- |
| `rule-on-dataset-field.yml` | A rule on a standard dataset's field, outside any rule set |
| `rule-outside-rule-set.yml` | A rule on a custom dataset, outside any rule set |
| `collection-rule-outside-rule-set.yml` | A collection severity on the dataset's collection, outside any rule set |
| `collection-rule-on-scalar.yml` | Collection severities on a dataset that is not a map, set or ordered collection |
| `changed-on-set.yml` | `changed` belongs to an ordered collection; a set uses `added` and `removed` |
| `age-on-bytes-in-rule-set.yml` | `age` is a datetime rule; the type check still applies inside a rule set |
| `no-projects.yml` | A root file names at least one project |
| `project-without-rule-set-name.yml` | A project maps each dataset to a rule-set name or `null`, not a list |
| `scope-is-gone.yml` | `scope` belonged to the single-project file |
| `history-window-of-one.yml` | A history window needs at least two commits |

## Validating

CI runs these checks in the `schema-fixtures` job. To run them locally, from the repository root:

```sh
R=examples/root-file
uvx check-jsonschema --check-metaschema schemas/root-file-v1.schema.json
uvx check-jsonschema --schemafile schemas/root-file-v1.schema.json $R/*.yml

# Each of these must report a validation error.
for f in $R/invalid/*.yml; do uvx check-jsonschema --schemafile schemas/root-file-v1.schema.json "$f"; done
```
