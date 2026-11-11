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
6. **The protocol library takes no package or framework references.** The protocol tests enforce
   it. Other projects under `src/` keep a short list of reviewed references.

## Not yet decided

- Where a Parquet reader would live.

## Current code

The protocol library has no references. The root file loader in `src/RootFile/` has one package
reference, YamlDotNet, read through its event parser only; the CLI has one framework reference,
to ASP.NET Core, for `present`. Every package and framework reference in the repository, including
those in test projects and script directives, is listed with a reason in
`.config/package-references.json`. The protocol tests fail on a reference that is not listed
and on a listed entry that no project uses, so adding a reference means editing that list. The
check reads `PackageReference` and `FrameworkReference` items in project, `.props` and `.targets`
files and `#:package` directives in `scripts/`; it does not read tool manifests or workflow
files.

The graph data model and validator live in `src/Graph/`, outside the protocol library, and
nothing outside the tests calls them yet. They still describe the earlier composition graph and
have not been reworked for the CI job graph.
