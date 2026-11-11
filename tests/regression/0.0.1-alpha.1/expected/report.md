# Diffra comparison report

- Delta ID: `sha256:cd940dcf3df12228dd85a9ffb6d9d43bfc79bed5ff9f73420d73ee173cbec4ab`
- Status: `changed`
- Baseline reference ID: `sha256:b446ef57d21b495c49d68e1dc079e3662ace2f9a58b484886f4f69567a8d42f6`
- Baseline evidence ID: `sha256:14e14a249ed2572eac60ba4ecaf6761e365120d733a323d1ed2ed43b55f21911`
- Candidate evidence ID: `sha256:976d5109904294bd9dff40075be119c4d69eb0986f5fdf998c6c647dea54618d`
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
