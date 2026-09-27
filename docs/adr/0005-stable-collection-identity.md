# ADR 0005: Stable collection identity

- Status: accepted
- Date: 2026-09-27

## Context

Some collections are sequences whose order matters. Others represent entities whose input order is incidental. Diffra cannot infer the distinction from JSON syntax alone.

## Decision

Arrays are ordered by default. A type schema may declare a collection keyed by a stable scalar key path and key type. Keyed collections require present, non-null, unique keys and declared key normalization. Compare keyed members by canonical key, ignoring input order. A changed key is one removal and one addition. Missing or duplicate keys invalidate the observation; the comparator never falls back to index matching.

## Consequences

Every evidence type documents collection semantics. Reordering is unchanged only for a declared keyed collection. Producers that cannot supply stable keys must use ordered semantics or reject the observation.
