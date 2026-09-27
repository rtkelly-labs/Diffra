# Diffra protocol v1

This page defines the portable JSON documents exchanged by Diffra v1. It is a design contract for the initial implementation; names and CLI flags may change before a package API is published.

## Documents and identity

Each document has a `schema` URI that fixes its document kind and an `id` in the form `sha256:<64 lowercase hexadecimal characters>`. The ID hashes a kind-specific projection with its own `id` omitted. Hash framing and field scope are defined in [trust and identity](trust-and-identity.md).

| Document | Purpose | Identity covers |
| --- | --- | --- |
| Evidence | Typed observations and artifact provenance | Type, subject, producer, relevant environment/provenance, payload digest, and artifact references |
| Baseline reference | Resolution of a selector to trusted evidence | Role, exact subject commit, manifest digest, Evidence ID, source store, and trust decision |
| Delta | Factual comparison result | Exact baseline and candidate IDs, comparator/configuration, status, and typed changes |
| Assessment | Policy findings about one Delta | Delta ID, policy/rules identity, outcome, and findings |

Operational timestamps, report formatting, temporary paths, and other nonsemantic details do not enter identities. V1 schemas keep volatile machine-local fields out of environment and provenance; the complete validated objects enter the Evidence identity.

## Evidence

An Evidence document has this shape:

```json
{
  "schema": "https://diffra.dev/schemas/evidence-v1.schema.json",
  "typeId": "org.example.generated-api",
  "typeVersion": "1.0.0",
  "typeResolution": "recognized",
  "subject": { "kind": "git-repository", "id": "repo:example/project", "commit": "<full-commit-id>" },
  "producer": { "id": "org.example.adapter", "version": "1.0.0", "configDigest": "sha256:..." },
  "environment": { "platform": "linux-x64", "runtime": ".NET 10.0", "tools": [{ "id": "dotnet-sdk", "version": "10.0.100" }] },
  "provenance": { "source": "git:<full-commit-id>", "artifactDigests": ["sha256:..."] },
  "status": "produced",
  "payloadDigest": "sha256:...",
  "payload": { "members": [] },
  "artifactRefs": [{ "id": "generated-source", "mediaType": "text/x-csharp", "digest": "sha256:...", "sizeBytes": 1200 }],
  "id": "sha256:..."
}
```

`typeId` is a lowercase reverse-DNS identifier for meaning, not file format. `typeVersion` is a semantic version. `typeResolution` is `recognized` or `opaque`. The `subject` carries a stable kind and ID plus an immutable `commit` when the subject is a Git revision. A moving branch name alone is not a revision. `producer.configDigest` identifies the exact effective producer configuration.

`payloadDigest` is SHA-256 over JCS canonical bytes of `payload`. Each `artifactRefs` record has a unique stable ID, media type, digest of exact artifact bytes, and `sizeBytes`. Payload and references validate against the document schema and the schema for the declared type version. A media type never selects the comparator.

Evidence `status` is `produced`, `unavailable`, `invalid`, or `inconclusive`. Non-produced statuses include a human-readable `message`. An unavailable optional record remains visible as unavailable. A required unavailable observation blocks a conclusive comparison. Malformed present evidence is invalid. Unknown type IDs or unsupported type versions may be stored with `typeResolution: "opaque"`; they cannot receive semantic comparison or a passing assessment.

## Baseline reference

A Baseline reference records the exact resolution of a selector:

```json
{
  "schema": "https://diffra.dev/schemas/baseline-reference-v1.schema.json",
  "role": "merge-base",
  "status": "trusted",
  "subjectCommit": "<full-merge-base-commit>",
  "manifestDigest": "sha256:...",
  "evidenceId": "sha256:...",
  "sourceStore": "local-cas",
  "resolver": { "id": "diffra.git-merge-base", "version": "1.0.0", "resolvedCommit": "<full-merge-base-commit>", "sourceRef": "refs/pull/7/merge" },
  "trust": { "issuer": "ci:trusted-main", "decisionId": "verification-42", "decisionDigest": "sha256:..." },
  "id": "sha256:..."
}
```

`role` is `merge-base`, `approved-main`, or `previous-release`. `subjectCommit`, `manifestDigest`, and `evidenceId` identify the resolved baseline. `resolver` records the resolver ID/version and resolved commit; `sourceRef` is lookup context only. A mutable value such as `main` never establishes identity. Baseline `status` is `trusted`, `candidate`, `untrusted`, `missing`, `corrupt`, or `incompatible`. Gate comparisons require `trusted`. A trusted reference also carries a `trust` record with issuer and decision ID; the decision digest is optional when the trust system does not issue a separate approval document.

## Delta

A Delta identifies the inputs and comparator. It contains no policy verdict:

```json
{
  "schema": "https://diffra.dev/schemas/delta-v1.schema.json",
  "baselineId": "sha256:...",
  "candidateEvidenceId": "sha256:...",
  "comparator": { "id": "org.diffra.structured", "version": "1.0.0", "configDigest": "sha256:...", "inputTypeId": "org.example.generated-api", "inputTypeVersion": "1.0.0" },
  "status": "changed",
  "changes": [{ "path": "/members/Widget.Read", "kind": "modified", "value": { "before": "int", "after": "long" } }],
  "typeResolution": "recognized",
  "provenance": {
    "baselineEvidenceId": "sha256:...",
    "candidateEvidenceId": "sha256:...",
    "environment": { "runtime": "dotnet" }
  },
  "id": "sha256:..."
}
```

Delta status is `unchanged`, `changed`, `unavailable`, `invalid`, `incompatible`, `inconclusive`, or `unsupported`. Each Delta also records `typeResolution` as `recognized` or `opaque`, and comparator input type ID/version. Provenance records baseline and candidate Evidence IDs plus comparison environment. A change states a fact, such as a keyed item being added, a numeric value moving from 12 to 16, or a text artifact differing. It may include typed before/after values, units, stable item identity, and source artifact references. It never contains `pass`, `warn`, or `fail`.

Use `invalid` when an input fails document, digest, schema, duplicate-key, or path validation. Use `unavailable` when required evidence or a baseline cannot be obtained. Use `incompatible` when both inputs exist but type/version, scope, methodology, or declared capabilities do not permit comparison. Use `unsupported` when the host has no implementation for a valid comparison type or mechanism. Use `inconclusive` when execution or a safe comparison cannot reach a result. None of these states claims equality. A valid compatible comparison with no changes is `unchanged`; one with changes is `changed`.

## Assessment

An Assessment cites one Delta and one exact policy revision:

```json
{
  "schema": "https://diffra.dev/schemas/assessment-v1.schema.json",
  "deltaId": "sha256:...",
  "deltaStatus": "changed",
  "policy": { "id": "org.example.api-policy", "version": "1.0.0", "rulesDigest": "sha256:..." },
  "outcome": "pass",
  "findings": [],
  "provenance": { "evaluator": "org.diffra.policy", "version": "1.0.0", "inputDeltaId": "sha256:..." },
  "id": "sha256:..."
}
```

Assessment outcome is `pass`, `warn`, `fail`, `needs-review`, or `inconclusive`. Invalid documents fail validation rather than receiving an outcome. Each finding carries a stable rule ID, status, message, and references to Delta change paths. A policy cannot alter a Delta. Re-evaluating a Delta under changed rules creates a new Assessment and leaves Evidence and Delta identities unchanged. A report without an Assessment describes measured change and has no policy verdict.

An invalid, unavailable, incompatible, inconclusive, or unsupported Delta cannot yield a passing Assessment. An incompatible or unsupported result may be rendered for review, but policy cannot claim it is unchanged.

## Type and version rules

An evidence type identifies a semantic contract, not an encoding. JSON and XML inputs can produce the same type/version when both validate to the same typed payload. Each type version defines its payload schema, subject and scope identity, units, numeric rules, null/missing distinctions, collection semantics, methodology requirements, and identity-relevant provenance.

V1 comparators accept only the exact same `typeId` and `typeVersion`. The host never guesses a migration from similar JSON shape. Cross-version migration execution is deferred until a versioned Delta schema can record the migration ID/version, configuration digest, and resulting type/version. Until then, a version mismatch is `incompatible`; a future registered migration that cannot preserve meaning must return `inconclusive`.

Extension registration is explicit in v1. Every decoder, comparator, migration, policy, store, resolver, or presenter declares its ID/version, input and output types, and configuration schema. The host rejects duplicate IDs, ambiguous comparator selection, missing dependencies, and incompatible contracts before execution. See the decisions in [`adr/`](adr/).

## JSON and collection semantics

Object member order and insignificant whitespace do not affect identity or structural comparison. Duplicate object names are invalid. Missing properties differ from `null`. Strings compare as exact Unicode scalar sequences unless the type declares normalization. Object member names sort by JCS UTF-16 code unit order. V1 accepts null, booleans, strings, arrays, objects, and integer number tokens only in `[-9007199254740991, 9007199254740991]`; it rejects fractional and exponent number tokens. Exact decimals and large integers use schema-declared decimal strings. This is a deliberate JCS-compatible subset, not full JCS number support.

Arrays are `ordered` unless the type schema declares a collection as `keyed`. Ordered arrays compare by position, so reordering is a change. A keyed collection declares a stable key path, scalar key type, and normalization. Every key must exist, be non-null, and be unique. Comparison matches members by canonical key and ignores input order. A key change is a removal plus an addition. Missing or duplicate keys make the observation invalid; the comparator never falls back to index matching.

## CLI flow

The executable v1 slice uses these separate steps. [README.md](../README.md) gives a runnable example with all `collect` identity flags.

```sh
diffra collect --payload metrics.json --subject-id example/project --commit <full-commit> --type-id org.example.metrics --type-version 1.0 --producer-id org.example.fixture --producer-version 1.0 -o evidence.diffra.json
diffra diff --baseline-ref baseline-reference.json --baseline-evidence baseline-evidence.json --evidence evidence.diffra.json -o delta.diffra.json
diffra eval --rules policy.diffra.json --delta delta.diffra.json -o assessment.diffra.json
diffra report delta.diffra.json --assessment assessment.diffra.json --baseline-ref baseline-reference.json --format html
```

The current `collect` validates a bounded structured numeric JSON payload and writes Evidence without artifact attachments. `diff` verifies supplied Evidence documents and a Baseline reference, checks numeric compatibility, and writes a policy-free Delta. It checks the reference's declared trust record structurally; authentication by a Git resolver or trusted store is a later phase, so this command must not treat a PR-supplied reference as trusted. `eval` assesses the unchanged Delta under JSON numeric rules. `report` renders the Delta and optional Assessment, and shows role and commit when given the Baseline reference. Each command writes its output atomically after validation and does not overwrite an input file.

Planned commands include `baseline verify`, `baseline update --mode missing|changed|all`, explicit approval/promotion, and `bundle export|import`. For the current CLI, exit 0 means a completed command or passing/warning Assessment, exit 3 means a failing, review-needed, or inconclusive Assessment, exit 2 means invalid input, and exit 1 means a file or execution error. `diff` records changed and unchanged in the Delta rather than using different exit codes.

## Threat boundaries

Treat evidence, schemas, manifests, paths, archives, external command output, and report text as untrusted input. Enforce configured byte, nesting, collection, and execution-time limits. Reject duplicate JSON keys, duplicate logical IDs, absolute paths, traversal, escaping symlinks, digest mismatches, and unsupported schema references.

External producers receive declared input paths and bounded output locations. Capture executable, version, argument vector, exit code, and output digests. Invoke a process without shell interpolation. Run untrusted producer code only within the CI isolation already provided by the adopter.

Escape all evidence-derived text in HTML. Do not execute scripts or load remote assets from evidence or report content. Renderers consume immutable Delta and Assessment documents and cannot change their identities or trust state. Baseline trust comes from the resolver and configured trust policy, never from a producer-controlled field.
