# ADR 0002: Explicit capability registration and version migration

- Status: accepted
- Date: 2026-09-27

## Context

Diffra must choose decoders, comparators, and migrations without guessing from file suffixes or finding plugins with undeclared behavior. Evidence types evolve, and equal JSON shape does not prove equal meaning.

## Decision

V1 discovers extensions through explicit code-first registration. Every capability declares a stable ID, version, input/output type IDs and versions, and configuration schema. The host rejects duplicate IDs, ambiguous comparator selection, missing dependencies, and incompatible contracts before execution. V1 comparators require equal type ID and version. Cross-version migration execution waits for a versioned Delta schema that can record migration ID/version, configuration digest, and output type/version. The host never infers a migration from JSON shape.

## Consequences

The first integration is more explicit to configure, but its behavior is auditable and repeatable. Unknown types remain opaque. No file suffix, matching field names, or compatible-looking JSON permits implicit comparison. V1 treats every type-version mismatch as incompatible.
