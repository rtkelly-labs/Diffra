# 0008: Optional event and telemetry output

- Status: accepted
- Date: 2026-11-11

## Context

The earlier design had every lifecycle stage emit a CloudEvents envelope and the CLI export
OpenTelemetry. No adopter consumes either, and sending data over the network from a pull request
run raises questions of credentials, failure handling and dependencies.

## Decision

1. **Both are opt-in and off by default.** When turned on, the CLI writes events and telemetry as
   JSON-lines files.
2. **The CLI does not send over the network by itself.** Sending is a separate, optional step.
3. **Sending uses the CI identity token.** Where a receiving service supports it, the sending job
   authenticates with the CI provider's short-lived identity token, not a stored secret. The
   service labels data by the token's event and ref claims, since the token proves which run sent
   the data, not that the data is correct.
4. **A failed send never fails the job.**

## Not yet decided

- Whether pull requests from forks can request the identity token (believed not, so those runs
  would stay file-only; to verify).
- Which OpenTelemetry file form to write, and how stable it is.
- New event type names that match the glossary.

## Current code

`CloudEvents.cs` and `schemas/cloudevent-diffra-v1.schema.json` define an envelope that no command
emits. There is no OpenTelemetry output.
