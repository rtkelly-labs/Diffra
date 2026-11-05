#!/usr/bin/env node

/**
 * External producer fixture demonstrating cross-ecosystem integration.
 * Emits a structured JSON payload to stdout matching Diffra expectations.
 */

import { argv } from 'node:process';
import { writeFileSync } from 'node:fs';

function parseArgs() {
  const args = {};
  for (let i = 2; i < argv.length; i++) {
    if (argv[i].startsWith('--') && i + 1 < argv.length) {
      args[argv[i]] = argv[++i];
    }
  }
  return args;
}

const args = parseArgs();
const payload = {
  schema: "https://diffra.dev/schemas/evidence-v1.schema.json",
  scope: args['--scope'] || "external-node-producer",
  methodology: "node-v25-runtime-telemetry",
  metrics: {
    nodeMemoryHeapUsedBytes: 15420128,
    nodeEventLoopLagMs: 2,
    activeHandlesCount: 4,
    workerThreadsCount: 0
  }
};

const jsonString = JSON.stringify(payload);

if (args['--output'] || args['-o']) {
  const outPath = args['--output'] || args['-o'];
  writeFileSync(outPath, jsonString + '\n', 'utf-8');
} else {
  process.stdout.write(jsonString + '\n');
}
