# Trust and identity

This reference defines how Diffra v1 names evidence, resolves baselines, and records approval. A trusted baseline is an immutable set of bytes and decisions, not a branch label.

## Canonical JSON subset

Diffra v1 canonical JSON uses the RFC 8785 JSON Canonicalization Scheme (JCS) rules for the subset the implementation accepts. V1 permits null, booleans, strings, arrays, objects, and integer number tokens in the range `[-9007199254740991, 9007199254740991]`. Reject fractional and exponent number tokens. Exact decimals and larger integers use schema-declared decimal strings with type-specific syntax and validation. This is a deliberate subset, not a claim to support canonical IEEE-754 number serialization.

Documents are UTF-8 without a byte-order mark. Reject invalid UTF-8 and duplicate object names. Sort object keys according to JCS UTF-16 code unit ordering, preserve array order, and do not normalize Unicode strings. Escape strings using JCS rules. Schema and policy documents are referenced by URI and separately identified by SHA-256 over their exact source bytes.

For any payload or identity projection, serialize the accepted JSON subset with these rules to canonical UTF-8 bytes. Compute digests as follows:

```text
payloadDigest = "sha256:" + lowercaseHex(SHA-256(JCS(payload)))
documentId = "sha256:" + lowercaseHex(
    SHA-256(ASCII("diffra/v1\0" + kind + "\0") || JCS(identityProjection)))
artifactDigest = "sha256:" + lowercaseHex(SHA-256(exactArtifactBytes))
```

`kind` is a fixed, case-sensitive, lowercase ASCII tag for the document kind:

| Document | `kind` tag |
| --- | --- |
| Evidence | `evidence` |
| Baseline reference | `baseline-reference` |
| Delta | `delta` |
| Assessment | `assessment` |
| Manifest | `manifest` |

The identity projection excludes the document's `id` field. Each document kind defines its projection below. Reject unknown document fields in v1 unless the schema explicitly permits them. The schema URI, type ID/version, comparator, policy, and configuration digests are strings in the projection; their source bytes have separate digests when required.

## Evidence identity

The Evidence identity projection includes exactly:

```text
schema, typeId, typeVersion, typeResolution, status,
subject, producer, environment, provenance, payloadDigest, payload, artifactRefs
```

V1 includes the complete schema-validated `environment` and `provenance` objects in Evidence identity. These objects have fixed schema fields; do not add timestamps, machine hostnames, temporary paths, or invocation IDs to them. Producer settings that can change the observation belong in `producer.configDigest`.

`payloadDigest` hashes the canonical `payload` value. Include both `payloadDigest` and `payload` in the Evidence projection so validation can detect a payload/digest mismatch before accepting its ID. Artifact digests hash exact stored bytes before decoding or normalization. Therefore differently formatted JSON may produce equal typed payload digests while its source artifact digest changes. Preserve both. `artifactRefs` records stable IDs, media types, exact-byte digests, and byte sizes. An opaque Evidence document still receives an identity over its validated envelope and raw artifact references, but it has no semantic comparison result.

The Baseline reference projection includes `schema`, `role`, top-level `status`, `resolver`, `sourceStore`, and, when present, `subjectCommit`, `manifestDigest`, `evidenceId`, and `trust`. A reference with status `missing` may omit `subjectCommit`, `manifestDigest`, `evidenceId`, and `resolver.resolvedCommit`, because resolution found nothing to identify; every other status requires them. A reference's `manifestDigest` is the `artifactDigest` of the exact stored manifest bytes, not the manifest's `id`. The Manifest projection includes `schema`, `subject`, `groups`, `artifacts`, `evidenceIds`, and `provenance`. The Delta projection includes `schema`, `status`, `typeResolution`, `baselineId`, `candidateEvidenceId`, `comparator`, `changes`, and `provenance`. The Assessment projection includes `schema`, `deltaId`, `deltaStatus`, `policy`, `outcome`, `findings`, and `provenance`. Each projection excludes only `id`; optional absent fields stay absent and do not serialize as null.

## Baseline resolution and trust

A Baseline reference records role, source store, selector, and, once resolved, exact subject commit, manifest digest, Evidence ID, and trust decision. Resolve mutable selectors to immutable revisions before comparison. For Git, record the full merge-base commit object ID; never use `main` as the baseline identity.

V1 baseline roles are:

| Role | Question answered |
| --- | --- |
| `merge-base` | What changed on this candidate branch? |
| `approved-main` | How does this candidate compare with the accepted state? |
| `previous-release` | What differs from the shipped release? |

Trust status is `trusted`, `candidate`, `untrusted`, `missing`, `corrupt`, or `incompatible`. Gate comparisons require `trusted`. `candidate` is reviewable but cannot satisfy a gate. `untrusted` means the source or approval is not accepted by the configured policy. `missing` means resolution found no required reference or bytes. `corrupt` means the referenced document or bytes fail digest/schema validation. `incompatible` means the reference exists but cannot serve the requested role or subject.

A trusted reference is bound to its trust policy through `trust.decisionDigest`: the digest of the promotion or approval record, which names the trust policy ID and version. The reference does not repeat the policy fields. The v1 schema keeps `decisionDigest` optional; a reference without it, such as one written by the current merge-base resolver, is not bound to a trust policy. A local first implementation may trust a baseline only when its commit is on the configured protected mainline and its manifest digest matches stored content. A reviewer or trusted CI can supply a stronger approval record later. A producer-controlled trust field never establishes trust.

## Candidate promotion

Candidate production and baseline promotion are separate operations. An update command writes candidate Evidence and a proposal. It cannot mark either trusted. Promotion requires:

1. A reviewer or trusted automation approves the exact candidate Evidence ID, subject commit, and applicable policy identity.
2. A clean second production run at the same subject commit reproduces the same Evidence ID and artifact digests.
3. The promotion approval record captures the decision. It contains, and its digest therefore covers, exactly `schema`, `decisionId`, `issuer`, `headCommit`, approved `evidenceId` and `payloadDigest`, `policyId`, `policyVersion`, `rulesDigest`, the `verificationRunEvidenceId` of the second run, and `timestamp`. It does not contain the Baseline reference or its ID.

The binding runs from the reference to the decision, not the other way round: a trusted reference's `trust.decisionDigest` is the digest of the approval record, and the reference ID covers `trust`. The approval record cannot name the reference ID, because that ID depends on the record's own digest.

Any change to the subject commit, Evidence ID, payload digest, or policy identity (ID, version, or rules digest) invalidates approval. A pull request job cannot promote its own output. A missing baseline cannot self-heal through candidate production.

## Delta and Assessment identity

A Delta projection covers its schema, exact `baselineId`, candidate Evidence ID, comparator ID/version/configuration digest and input type/version, status, typed changes, type resolution, and complete schema-validated comparison provenance. V1 requires exact type/version matches, so it has no migration identity field. It contains no policy verdict.

The comparison environment in Delta provenance names only what can change a comparison result. It records the runtime family (`"runtime": "dotnet"`), not the host patch version, so the same inputs and comparator produce the same Delta ID on every machine. Comparator logic changes are carried by the comparator version and configuration digest, which already enter the identity.

An Assessment projection covers schema, Delta ID/status, policy ID/version, rules digest, outcome, findings, and evaluator provenance. Re-evaluating a Delta under new rules changes only the Assessment ID. Rendering the same Delta and Assessment through a different renderer changes presentation identity only; it does not alter Evidence, Delta, Assessment, or baseline approval.

## Result states

These states carry different causes and cannot be collapsed into `unchanged` or `pass`:

| State | Meaning | Gate effect |
| --- | --- | --- |
| Evidence `invalid` | Present bytes, document, schema instance, or digest failed validation | Reject evidence; no semantic comparison |
| Evidence `unavailable` | A producer could not produce a required observation | Delta is unavailable or inconclusive; never pass |
| Delta `unavailable` | Required Evidence or baseline could not be obtained | No pass |
| Delta `invalid` | A present input failed validation | Reject input |
| Delta `incompatible` | Type/version, scope, methodology, or capability contracts do not permit comparison | No equality claim; V1 requires compatible input |
| Delta `unsupported` | A valid comparison request has no registered implementation | No equality claim; no pass |
| Delta `inconclusive` | Execution or safe comparison could not reach a result | Expose reason and provenance; no pass |
| Delta `unchanged` | A valid compatible comparison found no semantic change | Policy may pass, warn, fail, or require review |
| Delta `changed` | A valid compatible comparison found one or more changes | Policy evaluates the recorded changes |

An absent optional observation is represented by Evidence status `unavailable` and its declared optionality. An absent required observation prevents a conclusive Delta. A malformed present observation is `invalid`. A new or removed metric is an explicit typed change, not zero. A zero numeric baseline makes relative change undefined; the Delta may still report the absolute change. Assessment outcomes are `pass`, `warn`, `fail`, `needs-review`, and `inconclusive`. Invalid Assessment input is a validation error, not an outcome.

## Trust limits

Hash digests prove byte identity, not producer truth or safety. Schema validation proves shape, not semantic correctness. Record producer and toolchain identities and preserve source artifacts so a reviewer can inspect evidence. Keep parsers bounded, isolate external commands, and escape evidence text in reports. Approval asserts that exact evidence passed the named trust process; it does not make untrusted code safe to execute.
