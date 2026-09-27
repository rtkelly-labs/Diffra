# Diffra Artifact Delivery & Presentation Implementation Plan

> **For agentic workers:** Use `docs/artifact-delivery-and-ci.md`, `VISION.md`, and `PLAN.md` as specifications. Complete each task with build and test verification before proceeding.

**Goal:** Implement the presentation bundle directory materializer and the local loopback review server (`diffra present serve`) to allow offline, browser-based inspection of reports and assets without credentials or remote leaks.

**Architecture:**
- `Diffra.Protocol`: Inventory generator, delivery index validator, and relative asset link rewriter for HTML reports.
- `Diffra.Cli`: ASP.NET Core loopback server (`diffra present serve`) with CSP enforcement, plus presentation bundle packaging.
- Schemas: `schemas/delivery-index-v1.schema.json` for mapping artifact digests to release sources.

**Tech stack:** .NET 10, ASP.NET Core static files provider, `System.Text.Json`, JSON Schema draft 2020-12.

**Spec:** [`docs/artifact-delivery-and-ci.md`](../../artifact-delivery-and-ci.md), [`PLAN.md`](../../../PLAN.md).

## Global constraints

- No runtime network requests allowed during report rendering or presentation serving.
- Browser must never receive or require credentials (tokens, private URLs).
- Enforce strict Content Security Policy (`default-src 'self'`).
- Reject directory traversal (`..`), absolute paths, case-colliding paths, and escaping symlinks.
- Writes to bundle directories must be atomic via temporary directories.

## Tasks

### Task 1: Delivery Index & Review Bundle Schema & Models

**Files:** `schemas/delivery-index-v1.schema.json`, `src/Protocol/DeliveryIndex.cs`, `tests/Protocol.Tests/DeliveryIndexTests.cs`.

- [ ] Define JSON schema for `diffra-delivery-index.json` (repository, release tag, source commit, asset entries with SHA-256 and byte size).
- [ ] Implement typed models and canonical identity for `DeliveryIndex` and `Inventory`.
- [ ] Add tests verifying digest validation, unknown field handling, and sorting of inventory entries.

### Task 2: Review Bundle Materialization

**Files:** `src/Protocol/PresentationBundle.cs`, `tests/Protocol.Tests/PresentationBundleTests.cs`.

- [ ] Implement bundle layout generator creating `review-bundle/` (`index.html`, `diffra-delivery-index.json`, `inventory.json`, `documents/`, `assets/sha256/<digest>`).
- [ ] Rewrite HTML report asset references to use safe relative local paths (`assets/sha256/...`).
- [ ] Ensure atomic bundle creation: stage in temporary directory, verify all digests, and atomically move.
- [ ] Add tests verifying malformed assets, digest mismatches, and path traversal are rejected.

### Task 3: Local Presentation Server (`diffra present serve`)

**Files:** `src/Cli/PresentCommand.cs`, `src/Cli/Program.cs`, `tests/Protocol.Tests/ServeTests.cs`.

- [ ] Implement `diffra present serve <path>` using ASP.NET Core loopback web host (`127.0.0.1`).
- [ ] Restrict serving exclusively to the prepared directory using a PhysicalFileProvider.
- [ ] Add middleware enforcing CSP (`default-src 'self'`), disabling directory browsing, and rejecting unexpected Host headers.
- [ ] Add integration test verifying the server returns `index.html` with CSP headers and serves assets by SHA-256 path without external network access.

### Task 4: Content-Addressed Store & Bundle Export/Import

**Files:** `src/Protocol/ContentAddressedStore.cs`, `src/Cli/BundleCommands.cs`, `tests/Protocol.Tests/CasTests.cs`.

- [ ] Implement local immutable CAS with atomic writes and SHA-256 verification.
- [ ] Implement `diffra bundle export` (producing a self-contained `.diffra.tar.gz` or `.zip`) and `diffra bundle import`.
- [ ] Test round-trip export and import reproducing identical document identities.
