/// <reference types="vite/client" />
import { describe, expect, it } from 'vitest';

/**
 * Every runtime source file, loaded as raw text by Vite. The glob is evaluated
 * at build time, so this test needs no filesystem access (and no Node types).
 */
const sources = import.meta.glob<string>('../src/**/*.ts', {
  query: '?raw',
  import: 'default',
  eager: true,
});

/**
 * Remove block and line comments so documentation cannot hide or trigger the
 * checks. `//` inside a URL scheme (`https://`) is left alone, and block
 * comments become a space so tokens on either side never join.
 */
function stripComments(source: string): string {
  return source
    .replace(/\/\*[\s\S]*?\*\//g, ' ')
    .replace(/(?<!:)\/\/[^\n\r]*/g, '');
}

const files = Object.entries(sources);

describe('SDK sources avoid runtime-specific globals', () => {
  it('loads every source module', () => {
    expect(files.length).toBeGreaterThan(0);
    expect(files.map(([path]) => path)).toContain('../src/glitchport.ts');
    expect(files.map(([path]) => path)).toContain('../src/transport.ts');
  });

  it('contains no node: module specifier', () => {
    for (const [path, source] of files) {
      expect(stripComments(source), path).not.toMatch(/\bnode:/);
    }
  });

  it('contains no process identifier', () => {
    for (const [path, source] of files) {
      expect(stripComments(source), path).not.toMatch(/\bprocess\b/);
    }
  });

  it('contains no Buffer identifier', () => {
    for (const [path, source] of files) {
      expect(stripComments(source), path).not.toMatch(/\bBuffer\b/);
    }
  });
});
