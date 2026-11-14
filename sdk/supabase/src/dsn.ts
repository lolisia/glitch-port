import type { ParsedDsn } from './types.ts';

/** Shape every DSN is expected to have, used in error messages. */
const DSN_FORMAT = 'https://<publicKey>@<host>[:port]/<projectId>';

function invalidDsn(reason: string): Error {
  return new Error(`Invalid DSN: ${reason}`);
}

/**
 * Percent-decode a DSN component, turning malformed escapes into a descriptive
 * `Invalid DSN` error instead of a raw `URIError`.
 */
function decodeComponent(value: string, component: string): string {
  try {
    return decodeURIComponent(value);
  } catch {
    throw invalidDsn(`the ${component} contains invalid percent-encoding.`);
  }
}

/**
 * Parse a GlitchPort DSN into the collector URL, public key and project ID.
 *
 * Accepted form (http and https, optional port, optional trailing slash,
 * query and hash ignored):
 *
 *     https://<publicKey>@<host>[:port]/<projectId>
 *
 * @throws {Error} with an `Invalid DSN:` message when the DSN is malformed.
 */
export function parseDsn(dsn: string): ParsedDsn {
  const input = dsn.trim();
  if (input === '') {
    throw invalidDsn(`the DSN is empty (expected ${DSN_FORMAT}).`);
  }

  let parsed: URL;
  try {
    parsed = new URL(input);
  } catch {
    throw invalidDsn(`"${input}" is not a valid URL (expected ${DSN_FORMAT}).`);
  }

  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
    throw invalidDsn(
      `unsupported protocol "${parsed.protocol}" (expected "http:" or "https:").`,
    );
  }

  const publicKey = decodeComponent(parsed.username, 'public key');
  if (publicKey === '') {
    throw invalidDsn(`the public key (username) is missing (expected ${DSN_FORMAT}).`);
  }

  if (parsed.password !== '') {
    throw invalidDsn('a password is not allowed in the DSN.');
  }

  if (parsed.host === '') {
    throw invalidDsn(`the host is missing (expected ${DSN_FORMAT}).`);
  }

  // A DSN path is a single project segment; empty segments only appear as
  // duplicate/trailing slashes and are not projects of their own.
  const segments = parsed.pathname.split('/').filter((segment) => segment !== '');
  if (segments.length === 0) {
    throw invalidDsn(`the project ID is missing (expected ${DSN_FORMAT}).`);
  }
  if (segments.length > 1) {
    throw invalidDsn(
      `the project ID must be a single path segment, got "${segments.join('/')}" ` +
        `(expected ${DSN_FORMAT}).`,
    );
  }

  const projectId = decodeComponent(segments[0], 'project ID');
  if (projectId === '') {
    throw invalidDsn(`the project ID is empty (expected ${DSN_FORMAT}).`);
  }

  return {
    url: `${parsed.protocol}//${parsed.host}`,
    publicKey,
    projectId,
  };
}
