# 0009: Dependencies and extensions

- Status: accepted
- Date: 2026-11-11

## Context

The earlier rules forbade one framework by name and kept the core "free of heavyweight runtime
dependencies" without a test. The design now needs things such as a YAML parser, and possibly a
Parquet reader and an image decoder. The earlier design also composed Diffra in code, through a
code-first builder and a graph executor, which the root project file replaces.

## Decision

1. **Package references are limited.** Each one is reviewed; libraries that pull in large
   dependencies stay out of the core and live in separate packages built from this repository.
2. **The root file composes Diffra.** Code is added only for deterministic mechanisms the file
   cannot express.
3. **No code-first builder and no graph executor.** CI already schedules jobs; Diffra records the
   CI job graph as data ([0002](0002-project-file-and-datasets.md)). The existing graph data model
   and validator are kept, moved out of the protocol library, and reworked for the recorded CI
   graph. The executor and builder are deleted.
4. **Converters are declared, never guessed.** A dataset names its converter in the project file.
   Diffra never picks a converter from a file extension or from the shape of the JSON.
5. **A plugin model is future work.** If built, plugins are separate executables that write values
   in the `record` input format, which Diffra then validates.

## Not yet decided

- Whether the protocol library takes zero package references, enforced in CI, and the CLI keeps
  a short allow-list (proposed).
- Where a Parquet reader would live.

## Current code

No project under `src/` has a package reference; the CLI has one framework reference, to
ASP.NET Core, for `present`. `Graph.cs`, `GraphValidator.cs` and
`GraphExecutor.cs` in the protocol library implement a code-first composition graph that nothing
outside the tests calls.
