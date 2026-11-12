# Glossary

These are the words Diffra's documents, settings and output use. The
[decision records](adr/) build on them. Some current command and document names
(Evidence, Delta, Assessment, Baseline reference, metric) come from the earlier design;
the [feature status](README.md#feature-status) table shows what exists today.

| Term | Meaning |
| --- | --- |
| **Dataset** | One collected signal, with its type and the source it is read from: test results, lint findings, coverage, a table of benchmark results, a size, CI timing, a file. A dataset is collected and stored once and has one history. |
| **Dataset type** | The shape of a dataset's values, such as `diffra/test-results@1` or `diffra/table@1`: its fields, what their items are keyed by, and how they are compared. See [dataset types and converters](datasets.md). |
| **Converter** | The reader that turns one tool's own output format into a dataset type; built in, except Parquet, which is a separate package. A dataset names its converter in the root file; Diffra never guesses it. |
| **Project** | A named selection of datasets, with the rule set each one uses. A run names the project it collects for. One root file can define several projects. |
| **Record** | What one run stores for one commit, project and dataset: typed values, how they were collected, and any skipped, failed or rejected entries. Records are named by commit, project and dataset. |
| **Bundle** | A portable archive that carries records and results between jobs, into history, and to a reader. |
| **History** | The window of recent records on a branch, used to judge noisy values and to apply rules over time. |
| **Comparison** | What changed between a pull request's record and the record at its comparison point. A comparison carries no verdict. |
| **Rule** | A condition on a dataset, such as a threshold, a floor or a limit over the history window. Rules sit on the dataset in named rule sets. |
| **Finding** | One result of applying a rule, or a problem with the data itself (missing, rejected, cannot attribute). |
| **Attention level** | The single headline for a run: `act` ("Needs action"), `review` ("Worth reviewing") or `quiet` ("Quiet"). It comes from the findings only, not from the size of a change. |
