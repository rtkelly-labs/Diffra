# 0010: Line counts

- Status: proposed
- Date: 2026-11-17

## Context

The owner wants one consistent, fast way to count lines of code in CI across the estate, so that
each pull request shows what it changed per area. rtkelly13/rig has a one-off counter,
`tools/loc-report` (rig pull request 306). It writes a Markdown job summary and compares against
nothing. Diffra already compares keyed tables (`diffra/table@1`); what it lacks is a counter
([issue 159](https://github.com/rtkelly-labs/Diffra/issues/159)).

The well-known counters, scc, tokei and cloc, do not agree. Run on this repository's
[conformance corpus](../line-counts-conformance.md), all three give the same code, comment and
blank counts for 42 of 70 files, two agree on 24, and the three split on 4. They differ on Python
docstrings, Markdown, blank lines inside block comments, shebangs, strings that look like
comments, and empty or binary files. A counter that matches none of them would be one more opinion;
a counter that wraps one of them would need that binary in every CI job.

## Decision (proposed)

1. **Diffra counts lines itself.** A built-in converter, `lines`, fills a `diffra/table@1`
   dataset from the files a glob matches, the way the `files` converter fills `diffra/size@1`. It
   lives in `src/Converters/`, uses only the .NET base library and runs no external program.
2. **Diffra emulates the major tools as a black box.** Its rules come from what scc, tokei and
   cloc document and from what they are observed to output, never from their source code or
   language definition files. Diffra's language table is written here. (cloc is GPL-licensed;
   nothing of it is copied.)
3. **Their output is frozen as oracle fixtures.** A differential harness,
   `tests/LineCounts.Oracle`, runs pinned releases (cloc 2.12, scc 4.1.0, tokei 14.0.0) over a
   corpus of small files, each isolating one edge case, plus a few real files from this
   repository. It writes their per-file counts to `tests/line-counts/oracle.json`, with each
   case's SHA-256 and each tool's exact arguments. Normal CI never needs the tools: it runs the
   harness's `verify` command, which checks that every case has a frozen result from all three
   tools, that no case changed since it was frozen, that every case has a ruling, and that the
   conformance table is current. The harness's `check` command re-runs the tools and reports any
   drift; it runs on demand only, so a new release of a tool can be checked before it is pinned.
4. **Agree with the majority, or diverge on purpose.** The unit of agreement is a file's
   (code, comments, blanks) triple. The majority is the triple at least two of the three tools
   give exactly; a tool that does not count a file at all votes for not counting it. For every
   case, `tests/line-counts/rulings.json` records what Diffra's counter must give: the majority,
   or a deliberate divergence with its expected triple and its reason. A case where the three
   tools split has no majority and needs an explicit ruling. A ruling may be `open` while the
   design is reviewed, but none may stay open once the converter is built. The
   [conformance table](../line-counts-conformance.md) is generated from both files.
5. **Each line is code, comment or blank,** by a fixed, versioned language table: the extensions
   of each language, its line-comment and block-comment markers, its string delimiters, whether
   its block comments nest, and how it treats docstrings. A line holding any code is code, even
   when it ends in a comment. Text inside a string is never a comment. A blank line inside a
   block comment is a comment. An empty file is counted as a file with no lines, and a NUL byte
   does not make a file binary. Lines are ended by LF
   only, a CR before it being part of the line ending, as in all three tools. A UTF-8 byte-order
   mark is not text. The file is read as bytes, so text that is not valid UTF-8 is counted, not
   rejected.
6. **The language comes from the file name, by the converter's own table.** Decision 4 of
   [0009](0009-dependencies-and-extensions.md) says Diffra never picks a converter from an
   extension or from content. That still holds: the adopter names `lines`. Choosing a language
   inside the converter is the converter's documented rule, as the media-type table of the
   `files` converter for `diffra/files@1` is. The language is never guessed from content, so a
   shebang does not make a file without an extension count as a script. A matched file with no
   language in the table is not counted, and the entry states how many such files there were.
7. **Rows are areas; columns are counts.** The root file declares areas, and the converter writes
   one row per area, with `code`, `comments`, `blanks` and `files` columns, each a `count` with
   `better: none` by default. An adopter shrinking a codebase sets `code: { better: lower }`.
8. **A rule change is not a measured change.** Each entry names the counter's version and the
   digest of its language table. Two entries with different values are `incompatible`, as
   [protocol](../protocol.md) defines it, rather than compared.
9. **Untrusted input follows the [datasets](../datasets.md#untrusted-input) rules.** Links are not
   followed, and files and lines are bounded.

## Shapes proposed for review

These follow from the decisions but are proposals the owner has not yet seen.

The root file names the areas beside the columns:

```yaml
loc:
  type: diffra/table@1
  from: { lines: "src/**" }
  lines:
    exclude: ["**/bin/**", "**/obj/**"]
    areas:
      folders: { under: src, depth: 2 }
    unmatched: other
    byLanguage: true
  columns:
    code: { better: lower }
```

- `areas` is either `folders`, which makes each file's folder under `under`, cut to `depth`
  segments, its area (so `src/Rig.Cli/Agent/Sub/A.cs` is in `Rig.Cli/Agent`, and `src/Rig.Cli/B.cs`
  in `Rig.Cli`, as rig's report does today), or a list of `{ name, glob }` pairs, where the first
  pair whose glob matches a file takes it.
- A file no area takes goes to the row named by `unmatched`, or is left out when `unmatched` is
  not given, and the entry states how many were left out.
- With `byLanguage`, each row is split by language and keyed `<area>:<language>`, such as
  `Rig.Cli/Agent:C#`. The language names are the majority's: a `.tsx` file is `TypeScript`.
- `exclude` removes files the glob matches. scc and tokei skip what `.gitignore` names, and cloc
  does not; Diffra reads no ignore file (proposed), so build output such as `obj/` must be
  excluded by glob.

The first language table holds the languages rig needs, and their nearest neighbours: C# (`.cs`),
Python (`.py`, `.pyi`), TypeScript (`.ts`, `.tsx`, `.mts`, `.cts`), JavaScript (`.js`, `.jsx`,
`.mjs`, `.cjs`) and Markdown (`.md`, `.markdown`). Every language added later comes with corpus
cases and frozen results first.

## Not yet decided

The owner's rulings are needed on these before the converter is built:

1. **Python docstrings.** The three tools split three ways: cloc counts every triple-quoted string
   as a comment and a blank line in it as blank; scc counts a docstring as a comment, blank lines
   included, and any other triple-quoted string as code; tokei 14 counts docstrings as code. The
   proposal is to follow scc, which agrees with the majority on every other case.
2. **Markdown.** cloc and scc count prose as code, and tokei counts it as comments; the majority
   makes a Markdown row's `code` its non-blank lines. On HTML comments the tools split three ways;
   the proposal is to follow scc and count them as code too, so that Markdown has no comments.
3. **Doc comments.** None of the tools counts `///` or `/** */` apart from other comments, but
   rig's report does. Either `comments` covers both, as in all three tools, or the table gains a
   `docComments` column that is a part of `comments`.
4. **How many files one entry may read.** The entry schema allows 1,000 sources, and rig has
   about 1,360 C#, Python, TypeScript and Markdown files. The proposal is that a `lines` entry
   records one source, the digest of the sorted list of every path and SHA-256 it read, with its
   own safety limit of 20,000 files and 256 MiB in all. That changes the entry schema.
5. **Very long lines.** The text-format limit is 1 MiB per line, and a minified bundle can exceed
   it. The proposal is that such a file is not counted and the entry states it, rather than the
   whole entry being rejected.
6. **Where the version and table digest go.** The entry schema has no methodology field. Either
   the source's `format` carries it (`lines/1+<digest>`), or the entry gains a field.
7. **The area shapes above,** the key separator `:` and whether `.gitignore` should be honoured.
8. **Real trees from the estate.** The corpus holds three real files from this repository. Files
   from other repositories are not copied into this public one; the proposal is that the harness
   can be pointed at a checkout of any repository to report agreement, without freezing it.

## Current code

`tests/line-counts/corpus/` holds 70 cases, stored as `<name>.<ext>.case` so that no build,
linter or the repository's ban on Python files treats them as source, and marked `-text` in
`.gitattributes` so that line endings survive checkout. `tests/LineCounts.Oracle/` is the harness;
CI runs its `verify` command. `.github/workflows/line-counts-oracle.yml` installs the pinned tools
and runs `check` on demand. `tests/line-counts/oracle.json` holds the frozen results,
`tests/line-counts/rulings.json` the rulings, and
[line-counts-conformance.md](../line-counts-conformance.md) the generated table. The `lines`
converter is not built.
