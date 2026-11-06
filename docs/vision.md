# Diffra vision

Software projects produce evidence in many forms: generated code, API signatures,
metrics, packages, and screenshots. Diffra gives each observation a declared type
and an immutable identity. It compares compatible observations with an exact
baseline, records what changed, and evaluates those changes under separate rules.

The distinction matters. A change in a metric is a fact. Whether that change is
acceptable depends on a policy and its version. A report must show both without
turning an unknown or unavailable result into a pass.

## What Diffra owns

Diffra owns the portable Evidence, Baseline reference, Delta, and Assessment
documents. It owns their identity rules, compatibility checks, comparison results,
policy results, and deterministic report format. Producers keep ownership of the
tools that create observations. An adopter can add Diffra without moving its
existing tests or release gates into this repository.

Each baseline resolves to an exact subject commit and evidence identity. Approval
belongs to a trusted process outside a candidate pull request. A candidate can
propose evidence, but cannot declare its own reference trusted.

## Operating principle: Diffra utilizes Diffra

Diffra is built by utilizing Diffra. Every capability added to the system—typed observations,
deterministic deltas, quality policy evaluations, CAS bundles, and presentation artifacts—is
immediately integrated into Diffra's own development loop, test harness, and CI gates.

Diffra does not treat self-verification as an afterthought or a separate test fixture. The
codebase is its own first adopter and continuous client, ensuring that API boundaries, performance,
and developer experience are battle-tested against real changes before external adopters rely on them.

## The first useful workflow

The current .NET CLI accepts a bounded structured numeric payload. `collect`
creates Evidence, `diff` records a policy-free Delta, `eval` creates an Assessment,
and `report` renders the result. The [sample workflow](../examples/sample-project/workflow/)
contains inputs and checked-in outputs. The [protocol](protocol.md) describes the
document shapes and result states.

The local `diff` command checks the supplied Baseline reference's structure and
identity. It does not authenticate the issuer. A pull request supplied reference
must not become a CI gate until a trusted resolver and store establish its origin.

## What comes next

The next implementation steps are a Git-aware baseline resolver, immutable local
storage, candidate verification and promotion, and an integration with
Parquet.SourceGenerator. That integration should record generated source, API,
and metric evidence while keeping its current golden-file, compilation, and API
checks required. A second producer outside .NET will test the portable document
boundary.

Later comparators may handle keyed collections, structural documents, images,
archives, and distributions. Each one needs explicit semantics for scope, order,
units, and methodology. File extension alone does not choose a comparator.

The [handover vision](../VISION.md) describes the full proposed design. This page
states the goal against the implemented slice so readers can tell what exists.
