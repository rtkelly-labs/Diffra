# 0007: Packaging, providers and the Action

- Status: accepted
- Date: 2026-11-11

## Context

Every job that produces data runs Diffra, so installing it is a cost paid in each job, often in
repositories with no other use for .NET. The CLI currently references the ASP.NET Core framework
for its local review server, which no CI job needs. Support for more than one CI provider is an
intended direction.

## Decision

1. **Two builds.** A CI build without the web server, and a local build with it. The Action
   downloads the CI build on first use and caches it for later runs.
2. **Platform is the only compiled dimension.** No build varies by CI vendor.
3. **The core uses git and never a CI host.** The merge base, changed paths and branch tips come
   from git. Provider integrations supply the rest: run and job data, moving bundles between
   jobs, reading and writing history, and posting the comment.
4. **Provider code starts as Action steps.** When a provider's logic outgrows workflow steps, it
   moves into a small companion executable beside the one core CLI. The GitHub companion is
   named `diffra-github`.
5. **Optional converters are separate .NET programs** outside the native CI build. Diffra does not
   take on making third-party libraries work under ahead-of-time compilation.
6. **The Action's metadata file sits at the root of this repository.** Adopters reference
   `rtkelly-labs/Diffra@<commit>`. Action and CLI releases share tags.

## Not yet decided

- The Marketplace listing name ("Diffra" alone is not available, because a GitHub organization
  of that name exists) and whether to list before an install outside the owner's repositories.
- The name of the reusable workflow path.
- Whether the CI build is compiled ahead of time, an install-time limit per job, and checksum
  verification on download (all proposed).
- How adopters pin versions and what Diffra says on an upgrade pull request.

## Current code

The CLI ships as a .NET tool package that needs the ASP.NET Core runtime, and as self-contained
archives for five platforms. There is no Action and no reusable workflow. Provider-specific code
lives only in the self-evaluation scripts.
