# Publish Diffra on docs.ryankelly.dev

The intended public route is `https://docs.ryankelly.dev/diffra/`. Guides would
live below `/diffra/guides/`. Diffra remains the source of its Markdown files;
the `docs` repository renders them. A release of the portal must identify the
Diffra commit it ingested so a page can be traced back to its source.

## Current portal constraint

The `docs` repository is a Next.js static export deployed to GitHub Pages for
`docs.ryankelly.dev`. Its project registry and home page currently describe only
Parquet projects. Its ingest script reads a sibling repository from `localPath`
and writes JSON into `src/data`. The deployment workflow checks out only the
portal repository. Adding `../Diffra` to the registry alone would therefore
produce an empty stub on GitHub Actions.

The current guide parser also reads only top-level `docs/*.md` files. It labels
unnumbered files such as `vision.md` as `General`, while the project page lists
only Guides, Reference, and Internals. Relative Markdown links need a deliberate
route mapping when the portal imports these pages.

## Portal integration

Implement the portal change in its own pull request after the Diffra bootstrap
lands on `main`:

1. Add a `diffra` project entry and a developer-tools section to the portal
   registry, home page, and navigation. Keep Diffra out of Parquet counts and
   Parquet-specific copy.
2. Make the build obtain Diffra from `rtkelly-labs/Diffra` at a recorded commit.
   A pinned checkout in the deploy workflow or a committed ingest manifest can
   provide that input. Fail the build when a required source is missing rather
   than emitting stub pages.
3. Ingest the public top-level Markdown pages from this directory. Map each page
   to a visible portal category, preserve source links, and make relative links
   resolve within `/diffra/`. Keep ADRs and the adopter note linked from a
   documentation index until the portal supports nested pages.
4. Check the static export contains `/diffra/` and `/diffra/guides/vision/`.
   Inspect the rendered pages and links before deploying to the domain.

The portal's `repository_dispatch` hook can trigger a later rebuild when Diffra
changes. It does not supply source files by itself; the build still needs the
explicit checkout or pinned ingest input above.
