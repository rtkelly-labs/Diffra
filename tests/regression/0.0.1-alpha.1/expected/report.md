# Diffra comparison report

- Delta ID: `sha256:12be27dbf3c5f7018d798dd7ec8fdb870cde180cf8593470a2e8481b862bc52a`
- Status: `changed`
- Baseline reference ID: `sha256:651f3d7d58e2d9176b2dbdaa7071c30b72ba3542f7ef5cfcbe355173995f272f`
- Baseline evidence ID: `sha256:dd754f85c6194acc8313a372d153164d1f9b9cd10d70d0d48451b87ea98ca084`
- Candidate evidence ID: `sha256:c55d04bd6ff7d808043790f3a2c8f5a1b62fdb44e9bdc13f527af54d52efb9a0`
- Comparator: `diffra\.structured\-numeric` version `1\.0\.0`
- Baseline role: `merge\-base`
- Baseline resolved commit: `1111111111111111111111111111111111111111`

## Changes

| Path | Kind | Value | Unit |
| --- | --- | --- | --- |
| `/complexity` | `modified` | `\{"absolute":"2","after":"12","before":"10","relative":"0\.2","relativeStatus":"defined"\}` | cyclomatic\-complexity |
| `/latency` | `modified` | `\{"absolute":"60","after":"160","before":"100","relative":"0\.6","relativeStatus":"defined"\}` | ms |

## Policy assessment

- Policy: `org\.example\.budget` version `1\.0\.0`
- Outcome: `warn`

### Findings

- **pass** Numeric increase at '/complexity' is within policy limits\. (`numeric\-policy:/complexity`)
- **warn** Numeric increase at '/latency' exceeds the warn threshold\. (`numeric\-policy:/latency`)
