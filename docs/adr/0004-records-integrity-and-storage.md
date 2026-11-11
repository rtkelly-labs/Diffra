# 0004: Records, integrity and storage

- Status: accepted
- Date: 2026-11-11

## Context

Every document currently has a content-addressed ID: a SHA-256 over a canonical JSON projection
that includes the host environment. That served approval tied to identity, which is gone
([0003](0003-comparison-history-and-noise.md)). It makes an identical measurement change identity
when a runner is patched, and it pushes canonical encoding rules onto every emitter. CI jobs also
run on separate machines, so run data must travel between jobs, and history must live somewhere
the open tool can reach.

## Decision

1. **Records are named by commit, project and dataset.** Any run may write the record for a
   commit.
2. **The hash is an integrity check and a storage key.** Only the CLI computes it; emitters never
   do. The environment is recorded as data outside the hash. Byte-for-byte compatibility across
   languages is not promised until an adopter needs it. The code moves toward this in steps, not
   in one cut-over.
3. **Every record carries a format version.** A newer CLI skips records it cannot read and says
   so in one line with the count. History then rebuilds itself. There are no migration readers
   during alpha.
4. **History is stored through a pluggable strategy.** CI artifacts are the first
   implementation. A branch in the repository is a legitimate alternative. Per-commit records are
   the source of truth; a cumulative window may be carried forward for speed, is checked against
   git's commit list, and its gaps are repaired by re-reading per-commit records.
5. **Binary files are stored once per hash.** A record refers to a file by hash, size and media
   type. The bytes are kept once per distinct hash, with an index from hash to the run and
   artifact that hold them.
6. **One bundle format carries run data at every stage**: from a job to the collecting job, as
   the per-commit record, and as the full result with reports. A bundle holds typed values with
   dimensions, the job's identity, and skipped, failed and rejected entries. Raw source files are
   included only on request. The existing import checks (digests, declared contents, paths that
   escape the target folder) apply at every stage. A bundle has no Baseline reference.
7. **Each workflow file has its own collecting job.** It gathers the bundles from that
   workflow's jobs, runs the final step, and writes a partial record for the commit. Values are
   converted in the job that produced them.
8. **Schema identifiers are `https://` URLs on the project domain**, of the form
   `https://diffra.net/schemas/<name>-v<n>.schema.json`.

## Not yet decided

- The storage interface.
- Whether the wire documents are renamed to the glossary's terms now.
- How the collecting job merges bundles that disagree. The proposed rules are in
  [Recording a run](../record.md#merging-the-jobs-entries): identical duplicates are kept once, a
  later attempt of the same job replaces an earlier one, and two different values for the same
  project, dataset, key and dimensions are reported as a finding. A job's entries file is the
  first form of the job-stage bundle.
- What the output says when a stored binary file has expired.

## Current code

Document identity follows the earlier rule set out in [trust and identity](../trust-and-identity.md):
domain-separated SHA-256 over canonical JSON, environment included, decimals as strings.
`diffra bundle export|import` moves results between machines through a content-addressed store and
includes a Baseline reference. The self-evaluation workflow stores one file per commit on an
orphan branch.

`schemas/root-file-v1.schema.json` uses a `diffra.net` identifier. The other schemas and the
documents that cite them still use the earlier `diffra.dev` identifiers; changing them changes
stored output and the frozen regression corpus, which is a release decision.
