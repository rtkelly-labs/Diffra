# Root file examples

Worked examples of the root file described in
[decision record 0002](../../docs/adr/0002-project-file-and-datasets.md), validated against
[`schemas/root-file-v1.schema.json`](../../schemas/root-file-v1.schema.json). Nothing loads the
root file yet. The file's name in a repository is not decided, so these examples are named by
what they show.

The rule shapes (`check`, `versus`, `series`, `keys`, `when`) and the dataset types are those of
the earlier [project file](../project-file/). What changes is where rules sit and what a project
is:

- **Datasets** replace metrics. A dataset is collected and stored once and has one history.
  `required: false` marks one as optional.
- **Rule sets** sit on the dataset under `rules`, each with a name. A standard dataset keeps its
  data settings, such as `stratify`, under `fields` and its rules under
  `rules.<name>.fields`.
- **Projects** select datasets and map each one to the name of the rule set it applies. `scope`
  is gone.
- **Settings** hold the one-screen output limits (25 lines, 5 findings, 4 columns, about 500
  tokens for the agent-facing Markdown), the history window (20 commits) and the size limits.
  These numbers are the defaults; the examples spell some of them out.

The schema cannot check that a project names a rule set the dataset defines, or a dataset the
file declares. The loader will.

## Examples

| Example | Shows |
| --- | --- |
| [`release-train.yml`](release-train.yml) | Two projects over shared datasets: pull requests into main collect build and tests, releases add end-to-end results and judge the same data more strictly. An optional Windows dataset. |
| [`custom.yml`](custom.yml) | The earlier custom example rewritten for the root file: a custom variant type and custom leaf datasets read from the repository's own payload. |

## Invalid fixtures

Every file in [`invalid/`](invalid/) must fail validation against `root-file-v1`.

| Fixture | Why it fails |
| --- | --- |
| `rule-on-dataset-field.yml` | A rule on a standard dataset's field, outside any rule set |
| `rule-outside-rule-set.yml` | A rule on a custom dataset, outside any rule set |
| `age-on-bytes-in-rule-set.yml` | `age` is a datetime rule; the type check still applies inside a rule set |
| `no-projects.yml` | A root file names at least one project |
| `project-without-rule-set-name.yml` | A project maps each dataset to a rule-set name, not a list |
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
