import { describe, expect, it } from 'vitest';
import { parseDsn } from '../src/dsn.ts';

/**
 * Assert that `parseDsn` rejects `dsn` with an `Invalid DSN` error whose
 * message also mentions `pattern`.
 */
function expectInvalidDsn(dsn: string, pattern: RegExp): Error {
  let error: unknown;
  try {
    parseDsn(dsn);
  } catch (caught) {
    error = caught;
  }

  expect(error, `expected parseDsn(${JSON.stringify(dsn)}) to throw`).toBeInstanceOf(Error);
  const message = (error as Error).message;
  expect(message).toMatch(/^Invalid DSN: /);
  expect(message).toMatch(pattern);
  return error as Error;
}

describe('parseDsn', () => {
  it('parses an http DSN without a port', () => {
    expect(parseDsn('http://public@example.com/42')).toEqual({
      url: 'http://example.com',
      publicKey: 'public',
      projectId: '42',
    });
  });

  it('parses an https DSN without a port', () => {
    expect(parseDsn('https://public@glitchport.example.com/7')).toEqual({
      url: 'https://glitchport.example.com',
      publicKey: 'public',
      projectId: '7',
    });
  });

  it('keeps the port, accepts a trailing slash and ignores query and hash', () => {
    expect(parseDsn('  https://key@glitchport.example.com:8443/7/?foo=bar#section  ')).toEqual({
      url: 'https://glitchport.example.com:8443',
      publicKey: 'key',
      projectId: '7',
    });
  });

  it('keeps an http port', () => {
    expect(parseDsn('http://key@localhost:3000/42')).toEqual({
      url: 'http://localhost:3000',
      publicKey: 'key',
      projectId: '42',
    });
  });

  it('decodes the public key and the project ID', () => {
    expect(parseDsn('https://pub%40key@example.com/project%2D1')).toEqual({
      url: 'https://example.com',
      publicKey: 'pub@key',
      projectId: 'project-1',
    });
  });

  it('normalizes the host while returning the trimmed DSN values', () => {
    expect(parseDsn('HTTPS://PUBLIC@EXAMPLE.COM/42/')).toEqual({
      url: 'https://example.com',
      publicKey: 'PUBLIC',
      projectId: '42',
    });
  });

  it('rejects an empty DSN', () => {
    expectInvalidDsn('', /DSN is empty/);
    expectInvalidDsn('   ', /DSN is empty/);
  });

  it('rejects a DSN without a scheme', () => {
    expectInvalidDsn('public@example.com/42', /not a valid URL/);
    expectInvalidDsn('example.com/42', /not a valid URL/);
  });

  it('rejects a non-http(s) protocol', () => {
    expectInvalidDsn('ftp://public@example.com/42', /unsupported protocol "ftp:"/);
  });

  it('rejects a DSN without a public key', () => {
    expectInvalidDsn('https://example.com/42', /public key \(username\) is missing/);
    expectInvalidDsn('https://@example.com/42', /public key \(username\) is missing/);
  });

  it('rejects a DSN that contains a password', () => {
    expectInvalidDsn('https://public:secret@example.com/42', /password is not allowed/);
  });

  it('rejects a DSN without a project ID', () => {
    expectInvalidDsn('https://public@example.com', /project ID is missing/);
    expectInvalidDsn('https://public@example.com/', /project ID is missing/);
  });

  it('rejects a project path with more than one segment', () => {
    expectInvalidDsn('https://public@example.com/42/43', /single path segment/);
    expectInvalidDsn('https://public@example.com/42/43', /42\/43/);
  });

  it('rejects a public key with invalid percent-encoding', () => {
    expectInvalidDsn('https://pub%ZZ@example.com/42', /public key contains invalid percent-encoding/);
  });
});
