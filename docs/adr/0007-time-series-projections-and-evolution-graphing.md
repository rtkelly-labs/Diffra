# 0007: Time-Series Projections and Historical Evolution Graphing

- **Status**: Accepted
- **Date**: 2026-09-27
- **Deciders**: Ryan Kelly, Antigravity

## Context

Diffra's primary baseline comparison workflow evaluates pairwise deltas ($Candidate$ vs. $MergeBase$). However, software quality and performance are continuous trajectories over time:

1. **Longitudinal Trends**: Teams need visibility into how metrics (code coverage, binary footprint, cyclomatic complexity, benchmark latencies, cold start time) evolve across sequential commits and releases on the primary branch.
2. **Context for Decisions**: A PR adding 20 KB to a binary is difficult to judge in isolation; knowing whether the binary has grown by 400% over the last 90 days or remained completely stable changes the risk assessment.
3. **Visualization & Presentation**: Static numbers in Markdown tables fail to convey momentum or trajectory. Reviewers and stakeholders need intuitive visual trends in local and CI presentation bundles.

## Decision

We establish an immutable Time-Series Projection model (`diffra.timeseries.v1`) and design-system graphing generator:

1. **Append-Only Evidence Lineage**:
   - Because all Evidence documents stored in Diffra's Content-Addressed Store (CAS) are content-addressed and pinned to exact subject commits and timestamps, a sequence of promoted baselines on `main` forms an immutable time series.
   - Diffra queries the local or remote store by `(subjectId, typeId)` across a commit range or tag window to produce a deterministic time-series projection.

2. **Standard Time-Series Schema (`diffra.timeseries.v1`)**:
   - Explicit metric identifier, units, and methodology.
   - Chronologically ordered sequence of points:
     ```json
     {
       "commit": "3a1f9c8...",
       "timestamp": "2026-09-25T14:30:00Z",
       "tag": "v0.1.0",
       "value": 84.5
     }
     ```
   - Candidate overlay point allowing comparison of candidate PR metrics against the historical curve.

3. **Design-System Native SVG Graphing**:
   - Charts are rendered as clean, self-contained SVG vectors embedded directly into the review bundle's HTML and markdown reports.
   - Zero runtime JavaScript dependencies or external CDN resources, adhering strictly to Diffra's `default-src 'self'` Content Security Policy (CSP).
   - Styled with `@rtkelly13/design-system` design tokens:
     - `midnight` and `sketch` theme palettes.
     - Smooth Bezier curves, crisp baseline reference line, threshold bounds, and clear candidate delta callouts.
   - ASCII sparkline generation for CLI terminal output (`report` command).

4. **Self-Verification Principle**:
   - Diffra will chart its own metrics (file counts, coverage, schema versions) across commit history in its own review bundle.

## Consequences

- PR reviewers receive immediate historical context alongside the immediate delta.
- Review bundles remain 100% self-contained, offline-first, and secure (no external charting JS).
- Time-series datasets can be exported or queried for broader estate governance and dashboards.
