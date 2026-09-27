# ADR 0001: Canonical JSON and hash identity

- Status: accepted
- Date: 2026-09-27

## Context

Evidence documents must have the same identity across languages and machines. Raw serialized JSON bytes vary with whitespace and object property order. Hashing a document that contains its own digest is recursive.

## Decision

Use UTF-8 JSON canonicalized with the RFC 8785 JCS rules for the accepted subset: null, booleans, strings, arrays, objects, and integer tokens from -9007199254740991 through 9007199254740991. Reject fractions, exponent tokens, duplicate keys, and invalid UTF-8. Exact decimals and larger integers use schema-declared decimal strings. Hash a schema-kind-specific identity projection that excludes `id`, using `SHA-256(ASCII("diffra/v1\0" + kind + "\0") || JCS(projection))`. Prefix digests with `sha256:`. Artifact digests cover exact bytes; `payloadDigest` hashes JCS bytes of payload; Evidence ID covers every validated Evidence field except `id`.

## Consequences

Equivalent accepted objects with different insignificant whitespace or property order have equal semantic identity. Raw artifact digests may still differ. Schema/config source bytes retain exact digests. Implementations must share fixtures for canonicalization and identity projections before claiming wire compatibility.
