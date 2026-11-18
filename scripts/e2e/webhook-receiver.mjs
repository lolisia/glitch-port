#!/usr/bin/env node
//
// Minimal webhook receiver used by the end-to-end alert script.
//
// The server listens on an arbitrary free port (unless --port pins one), appends
// every accepted POST body to a JSONL file as exactly one JSON line, answers
// 200 and prints its chosen port on stdout as "listening on <port>" so the
// shell script can build the alert rule target URLs. Non-POST requests are
// rejected with 405 and are never recorded. A body that is not valid JSON is
// stored as a JSON string, so every line of the JSONL file stays valid JSON.
//
// Usage:
//   node scripts/e2e/webhook-receiver.mjs --output .artifact/e2e/e2e-alerts-webhooks.jsonl
//
// Options:
//   --output, -o <file>   JSONL file that receives one line per webhook body
//   --host <address>      address to bind (default 127.0.0.1)
//   --port, -p <number>   port to bind; 0 (the default) picks any free port
//   --help, -h            print this help and exit
//
// Requires Node.js 22 (standard library only, no dependencies).

import { appendFileSync, mkdirSync } from "node:fs";
import { createServer } from "node:http";
import { dirname, resolve } from "node:path";

/** Largest accepted request body, matching the Relay's decompressed limit. */
const maxBodyBytes = 1024 * 1024;

const usage = `usage: node scripts/e2e/webhook-receiver.mjs --output <file> [options]

Records every POST request body as one JSON line in the JSONL file and answers
200. Non-POST requests are rejected with 405.

  --output, -o <file>   JSONL file that receives one line per webhook body
  --host <address>      address to bind (default 127.0.0.1)
  --port, -p <number>   port to bind; 0 (the default) picks any free port
  --help, -h            print this help and exit`;

class UsageError extends Error {}

/** Parses the command line without any dependency. */
function parseArgs(args) {
  const options = {
    output: null,
    host: "127.0.0.1",
    port: 0,
  };

  const readValue = (name, index) => {
    if (index >= args.length) {
      throw new UsageError(`${name} requires a value`);
    }

    return args[index];
  };

  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    let value;

    if (arg === "--help" || arg === "-h") {
      options.help = true;
      return options;
    }

    if (arg === "--output" || arg === "-o") {
      value = readValue(arg, index + 1);
      options.output = value;
      index += 1;
      continue;
    }

    if (arg.startsWith("--output=")) {
      options.output = arg.slice("--output=".length);
      continue;
    }

    if (arg === "--host") {
      options.host = readValue(arg, index + 1);
      index += 1;
      continue;
    }

    if (arg.startsWith("--host=")) {
      options.host = arg.slice("--host=".length);
      continue;
    }

    if (arg === "--port" || arg === "-p") {
      value = readValue(arg, index + 1);
      options.port = parsePort(value);
      index += 1;
      continue;
    }

    if (arg.startsWith("--port=")) {
      options.port = parsePort(arg.slice("--port=".length));
      continue;
    }

    throw new UsageError(`unknown argument: ${arg}`);
  }

  if (typeof options.output !== "string" || options.output.trim() === "") {
    throw new UsageError("--output is required");
  }

  if (typeof options.host !== "string" || options.host.trim() === "") {
    throw new UsageError("--host must not be blank");
  }

  return options;
}

/** Validates one --port value. */
function parsePort(value) {
  const port = Number(value);

  if (!Number.isInteger(port) || port < 0 || port > 65535) {
    throw new UsageError(`--port must be an integer between 0 and 65535: ${value}`);
  }

  return port;
}

/** Raised when a request body exceeds {@link maxBodyBytes}. */
class BodyTooLargeError extends Error {
  constructor() {
    super(`the request body must not exceed ${maxBodyBytes} bytes`);
    this.name = "BodyTooLargeError";
  }
}

/** Reads the complete request body, rejecting bodies above the size limit. */
function readBody(request) {
  return new Promise((resolvePromise, rejectPromise) => {
    const chunks = [];
    let size = 0;
    let settled = false;

    const settle = (callback, value) => {
      if (settled) {
        return;
      }

      settled = true;
      callback(value);
    };

    request.on("data", (chunk) => {
      if (settled) {
        return;
      }

      size += chunk.length;
      if (size > maxBodyBytes) {
        // Keep draining so the client still receives the 413 response.
        request.removeAllListeners("data");
        request.resume();
        settle(rejectPromise, new BodyTooLargeError());
        return;
      }

      chunks.push(chunk);
    });

    request.on("end", () => settle(resolvePromise, Buffer.concat(chunks)));
    request.on("error", (error) => settle(rejectPromise, error));
  });
}

/**
 * Converts a request body to one compact JSON line. A body that is not valid
 * JSON is recorded as a JSON string, so the JSONL file never contains a broken
 * line.
 */
function serializeBody(body) {
  const text = body.toString("utf8");

  try {
    return JSON.stringify(JSON.parse(text));
  } catch {
    return JSON.stringify(text);
  }
}

/** Sends one JSON response unless the response was already started. */
function respondJson(response, statusCode, payload, extraHeaders = {}) {
  if (response.headersSent || response.writableEnded) {
    return;
  }

  const body = JSON.stringify(payload);

  response.writeHead(statusCode, {
    "Content-Type": "application/json; charset=utf-8",
    "Content-Length": Buffer.byteLength(body),
    ...extraHeaders,
  });
  response.end(body);
}

/** Starts the receiver and prints the port the shell script needs. */
function main() {
  let options;

  try {
    options = parseArgs(process.argv.slice(2));
  } catch (error) {
    if (error instanceof UsageError) {
      console.error(`webhook-receiver: ${error.message}`);
      console.error(usage);
      process.exit(2);
    }

    throw error;
  }

  if (options.help) {
    console.log(usage);
    return;
  }

  const outputFile = resolve(options.output);
  mkdirSync(dirname(outputFile), { recursive: true });

  const server = createServer((request, response) => {
    if (request.method !== "POST") {
      request.resume();
      respondJson(response, 405, { error: "only POST is supported" }, { Allow: "POST" });
      return;
    }

    readBody(request)
      .then((body) => {
        const line = serializeBody(body);

        // Write before answering so a recorded webhook is always visible to a
        // script that counts the JSONL lines as soon as the request returns.
        appendFileSync(outputFile, `${line}\n`, "utf8");
        console.log(
          `webhook-receiver: received POST ${request.url} (${Buffer.byteLength(body)} bytes)`,
        );
        respondJson(response, 200, { received: true });
      })
      .catch((error) => {
        if (error instanceof BodyTooLargeError) {
          respondJson(response, 413, { error: error.message });
          return;
        }

        console.error(`webhook-receiver: could not record the request: ${error.message}`);
        respondJson(response, 500, { error: "could not record the request" });
      });
  });

  server.on("error", (error) => {
    console.error(`webhook-receiver: server error: ${error.message}`);
    process.exit(1);
  });

  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);

  let closing = false;

  function shutdown() {
    if (closing) {
      return;
    }

    closing = true;
    server.close(() => process.exit(0));
    server.closeAllConnections?.();
  }

  server.listen(options.port, options.host, () => {
    const address = server.address();
    const port = address !== null && typeof address === "object" ? address.port : options.port;

    console.log(`webhook-receiver: recording bodies in ${outputFile}`);
    console.log(`listening on ${port}`);
  });
}

main();
