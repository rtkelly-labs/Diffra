# Sample project documents

The JSON files in this directory are schema-shape examples with placeholder digests. They demonstrate allowed fields and a rejected `verdict` in Delta; they are not input to the executable workflow.

[`workflow/`](workflow/) holds real documents created by the CLI and a fixture-only Baseline reference. The check runner verifies the document IDs, the manifest byte digest, the chain between Evidence, Baseline, Delta, and Assessment, and the exact HTML report bytes. Run `uv run python generate_fixture_reference.py` from `workflow/` only to regenerate the local demonstration reference after changing `baseline-evidence.json`. This script does not approve or promote a production baseline.
