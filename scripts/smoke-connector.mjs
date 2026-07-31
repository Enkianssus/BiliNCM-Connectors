import { spawn } from 'node:child_process';

const [executable, expectedConnectorId] = process.argv.slice(2);
if (!executable || !expectedConnectorId) {
  throw new Error(
    'Usage: node scripts/smoke-connector.mjs <executable> <connector-id>'
  );
}

const requestId = `smoke-${process.pid}-${Date.now()}`;
const child = spawn(executable, [], {
  windowsHide: true,
  stdio: ['pipe', 'pipe', 'pipe']
});

let stdout = '';
let stderr = '';
let finished = false;
let pingResult = null;

const timeout = setTimeout(() => {
  finish(new Error(`Connector smoke test timed out. stderr=${stderr.trim()}`));
}, 10_000);

function finish(error) {
  if (finished) return;
  finished = true;
  clearTimeout(timeout);
  if (child.exitCode === null && !child.killed) child.kill();
  if (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}

child.stderr.setEncoding('utf8');
child.stderr.on('data', chunk => {
  stderr += chunk;
});

child.stdout.setEncoding('utf8');
child.stdout.on('data', chunk => {
  stdout += chunk;
  let newline = stdout.indexOf('\n');
  while (newline >= 0) {
    const line = stdout.slice(0, newline).trim();
    stdout = stdout.slice(newline + 1);
    newline = stdout.indexOf('\n');
    if (!line) continue;

    let response;
    try {
      response = JSON.parse(line);
    } catch {
      continue;
    }
    if (response.id === requestId) {
      const result = response.result;
      if (
        response.ok !== true
        || result?.protocolVersion !== 1
        || result?.connectorId !== expectedConnectorId
        || typeof result?.capabilities !== 'object'
      ) {
        finish(new Error(`Invalid ping response: ${line}`));
        return;
      }

      pingResult = result;
      child.stdin.write(
        `${JSON.stringify({ id: `${requestId}-shutdown`, action: 'shutdown' })}\n`
      );
      continue;
    }

    if (response.id === `${requestId}-shutdown`) {
      if (response.ok !== true || response.result?.stopped !== true) {
        finish(new Error(`Invalid shutdown response: ${line}`));
        return;
      }
      console.log(
        `Connector smoke test passed: ${pingResult.connectorId} `
        + `v${pingResult.connectorVersion}`
      );
      finish();
    }
  }
});

child.on('error', error => finish(error));
child.on('exit', code => {
  if (!finished && code !== 0) {
    finish(
      new Error(
        `Connector exited with code ${code}. stderr=${stderr.trim()}`
      )
    );
  }
});

child.stdin.write(
  `${JSON.stringify({ id: requestId, action: 'ping' })}\n`
);
