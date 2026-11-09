# Graph Host, DAG Validation, and External Command Adapter Implementation Plan

> **Goal**: Implement Phase 2 of `PLAN.md` ([Issue #9](https://github.com/rtkelly-labs/Diffra/issues/9)): explicit code-first registration, DAG cycle/type validation, stable execution with cascading fault status, an external command adapter with bounded I/O, and cross-ecosystem fixtures.

## Proposed Architecture

1. **Graph Domain Model & Builder (`src/Protocol/Graph.cs`)**:
   - `GraphNode`: Identifies a node by `Id`, `NodeKind` (Subject, Producer, Baseline, Comparator, Policy, Presenter, Store), `InputType`, `OutputType`, and `Dependencies`.
   - `GraphDefinition`: Immutable collection of nodes representing the declared computation graph.
   - `GraphBuilder`: Fluent code-first API to register subjects, producers, baselines, comparators, policies, stores, and presenters.

2. **DAG Validator (`src/Protocol/GraphValidator.cs`)**:
   - Strictly validates before execution:
     - Cycle detection (DFS cycle path detection with readable cycle trace).
     - Missing inputs (unresolved dependency IDs).
     - Duplicate IDs (duplicate node registrations).
     - Incompatible types (upstream output type != downstream expected input type).
     - Disallowed baseline roles and ambiguous comparators.
   - Fail-fast with zero side effects: Throws `GraphValidationException` with all discovered violations.

3. **DAG Execution Engine (`src/Protocol/GraphExecutor.cs`)**:
   - Determines deterministic topological execution order.
   - Executes nodes sequentially (or parallel where dependencies permit).
   - Manages node state (`Pending`, `Running`, `Completed`, `Failed`, `Blocked`).
   - If an upstream node fails, downstream dependent nodes are marked `Blocked` / `Inconclusive` with clear provenance.

4. **External Command Adapter (`src/Protocol/ExternalCommandAdapter.cs`)**:
   - Spawns external executables without shell interpolation (`UseShellExecute = false`, safe `ArgumentList`).
   - Bounded execution timeouts, bounded output buffers, and exit code validation.
   - Ingests stdout or output file into canonical `EvidenceDocument` with cryptographic payload digest verification.

5. **Cross-Ecosystem Fixtures & Self-Verification**:
   - In-process extension fixture + external producer fixture yielding identical canonical identity.
   - Utilize Diffra Graph Host in Diffra's own dogfooding pipeline (`scripts/RunDogfood.cs`).

## Tasks

- [ ] Task 1: Core Graph Models and Builder (`src/Protocol/Graph.cs`)
- [ ] Task 2: Strict DAG Validator (`src/Protocol/GraphValidator.cs`)
- [ ] Task 3: Graph Execution Engine (`src/Protocol/GraphExecutor.cs`)
- [ ] Task 4: External Command Adapter (`src/Protocol/ExternalCommandAdapter.cs`)
- [ ] Task 5: Unit and Integration Test Suite (`tests/Protocol.Tests/GraphTests.cs`)
- [ ] Task 6: Dogfooding Migration and CI Verification
EOF
