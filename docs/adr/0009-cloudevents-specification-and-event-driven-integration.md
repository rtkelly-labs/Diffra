# 0009: CNCF CloudEvents Specification & Event-Driven Integration

- **Status**: Accepted
- **Date**: 2026-09-27
- **Deciders**: Ryan Kelly, Antigravity
- **Standard**: CNCF CloudEvents v1.0.2

## Context

In ADR 0008, we established the wide-event philosophy (Charity Majors, Honeycomb) and OpenTelemetry (OTel) emission for live telemetry. However, modern CI/CD, release engineering, and platform orchestration increasingly operate as **event-driven architectures**:

1. **Ecosystem Interoperability**: Consumers want to route Diffra observations to AWS EventBridge, Azure Event Grid, GCP Eventarc, Apache Kafka, NATS, or custom webhooks to trigger downstream workflows (e.g. automated baseline promotion, Slack/Teams notifications, deployment gating, Jira/Linear issue tracking).
2. **Standardized Event Envelope**: While Diffra defines domain-specific schemas (`evidence-v1`, `delta-v1`, `assessment-v1`), event brokers and webhook consumers expect a standard, vendor-neutral envelope structure.
3. **Synergy with Wide Events & OTel**: CloudEvents provides the standard transport envelope for distributing wide events, while OTel provides the tracing/telemetry pipeline. The CNCF officially defines bidirectional interoperability between CloudEvents and OpenTelemetry.

## Decision

We adopt the **CNCF CloudEvents v1.0.2 specification** as Diffra's standard distribution and webhook envelope:

### 1. CloudEvents Envelope Mapping

Every Diffra lifecycle stage emits a standard CloudEvents envelope (`application/cloudevents+json`):

| Diffra Concept | CloudEvents Attribute | Example / Mapping |
|---|---|---|
| Specification Version | `specversion` | `"1.0"` |
| Document Identity | `id` | Canonical SHA-256 digest (`sha256:d5582e09...`) |
| Origin Context | `source` | URI: `diffra://repo/{repo}/subject/{subjectId}` |
| Event Type | `type` | `dev.diffra.evidence.v1`, `dev.diffra.delta.v1`, `dev.diffra.assessment.v1` |
| Evaluation Time | `time` | RFC 3339 timestamp (e.g. `2026-09-27T19:25:00Z`) |
| Data Content Type | `datacontenttype` | `"application/json"` |
| Data Schema | `dataschema` | URL: `https://diffra.dev/schemas/{schema}.schema.json` |
| Subject Anchor | `subject` | Git commit or PR ref (e.g. `commit:3a3d8b9...`) |
| Event Payload | `data` | The complete canonical Evidence, Delta, or Assessment document |

### 2. Extension Attributes

To enable lightweight routing and filtering at the event broker level without parsing the nested `data` payload:
- `diffraoutcome`: `"pass"`, `"warn"`, `"fail"` (on assessment events)
- `diffradeltastatus`: `"unchanged"`, `"changed"`, `"unavailable"`, etc.
- `diffragithash`: Full 40-character Git commit SHA
- `diffragitbranch`: Target branch (e.g. `"main"`)
- `diffragithubpr`: PR number if triggered in a pull request

### 3. Overlap with Wide Events and OTel

- **CloudEvents = The Distribution Envelope**: Formats the event for webhooks, message queues, and serverless functions.
- **Wide Event = The Event Content (`data`)**: Contains the high-cardinality, un-aggregated observation dimensions.
- **OpenTelemetry = The Trace Context**: CloudEvents supports the W3C `traceparent` and `tracestate` extension attributes, allowing a Diffra CloudEvent to be correlated with the distributed trace of the CI build that produced it.

## Consequences

- Direct plug-and-play with enterprise event brokers (Kafka, EventBridge, Event Grid).
- Downstream systems can filter on `diffraoutcome == "fail"` directly in message broker rules without inspecting payload JSON.
- W3C trace context preservation maintains full lineage between CI runs, Diffra evaluations, and webhook consumers.
