"""Generate a local test reference. This script is not a trust or promotion mechanism.

Run with `uv run python generate_fixture_reference.py` from this directory.
The fixture simulates a trusted resolver so the four CLI steps can be exercised.
"""

import hashlib
import json
from pathlib import Path


ROOT = Path(__file__).resolve().parent
SAFE_INTEGER = 9_007_199_254_740_991


def canonical(value):
    """JCS-compatible bytes for this fixture's ASCII and safe-integer JSON subset."""
    if value is None or isinstance(value, bool):
        return json.dumps(value, separators=(",", ":")).encode("ascii")
    if isinstance(value, int):
        if abs(value) > SAFE_INTEGER:
            raise ValueError("unsafe JSON integer")
        return str(value).encode("ascii")
    if isinstance(value, str):
        value.encode("ascii")
        return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("ascii")
    if isinstance(value, list):
        return b"[" + b",".join(canonical(item) for item in value) + b"]"
    if isinstance(value, dict):
        keys = sorted(value)
        return b"{" + b",".join(canonical(key) + b":" + canonical(value[key]) for key in keys) + b"}"
    raise ValueError(f"unsupported fixture value: {type(value).__name__}")


def digest(data):
    return "sha256:" + hashlib.sha256(data).hexdigest()


def document_id(kind, document):
    projection = {key: value for key, value in document.items() if key != "id"}
    return digest(b"diffra/v1\0" + kind.encode("ascii") + b"\0" + canonical(projection))


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def main():
    evidence = json.loads((ROOT / "baseline-evidence.json").read_text(encoding="utf-8"))
    subject = evidence["subject"]
    manifest = {
        "schema": "https://diffra.dev/schemas/manifest-v1.schema.json",
        "subject": subject,
        "groups": [{
            "id": "quality", "name": "Quality", "role": "quality", "required": True,
            "baselineRole": "merge-base", "retention": "fixture", "gate": "required",
            "artifactIds": [], "evidenceIds": [evidence["id"]], "groups": [],
        }],
        "artifacts": [],
        "evidenceIds": [evidence["id"]],
        "provenance": {
            "producerId": "org.example.fixture", "producerVersion": "1.0",
            "environment": {"platform": evidence["environment"]["platform"], "runtime": evidence["environment"]["runtime"]},
            "source": evidence["provenance"]["source"],
        },
    }
    manifest["id"] = document_id("manifest", manifest)
    manifest_path = ROOT / "baseline-manifest.json"
    write_json(manifest_path, manifest)

    reference = {
        "schema": "https://diffra.dev/schemas/baseline-reference-v1.schema.json",
        "role": "merge-base", "status": "trusted", "evidenceId": evidence["id"],
        "subjectCommit": subject["commit"], "manifestDigest": digest(manifest_path.read_bytes()),
        "resolver": {"id": "org.example.fixture-resolver", "version": "1.0", "resolvedCommit": subject["commit"], "sourceRef": "local-fixture"},
        "sourceStore": "local-fixture",
        "trust": {"issuer": "fixture-only", "decisionId": "demonstration", "decisionDigest": digest(b"fixture-only")},
    }
    reference["id"] = document_id("baseline-reference", reference)
    write_json(ROOT / "baseline-reference.json", reference)


if __name__ == "__main__":
    main()
