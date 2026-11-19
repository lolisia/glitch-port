/// <reference types="node" />
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { cwd } from 'node:process';
import { describe, expect, it } from 'vitest';

// Vitest stubs CSS imports (even with a raw query), so the stylesheet is read
// from disk to assert on its actual contents.
const indexCss = readFileSync(resolve(cwd(), "src/index.css"), 'utf8');

/**
 * Guards the post-migration stylesheet: the shadcn token blocks stay, while
 * the legacy `--color-*` aliases and hand-written global screen rules do not
 * come back.
 */
describe('theme stylesheet', () => {
  it('defines the shadcn tokens for light and dark', () => {
    expect(indexCss).toContain(':root {');
    expect(indexCss).toContain(":root[data-theme='dark']");
    expect(indexCss).toContain('--background:');
    expect(indexCss).toContain('--primary:');
    expect(indexCss).toContain('--sidebar-ring:');
    expect(indexCss).toContain('--success:');
    expect(indexCss).toContain('--warning:');
    expect(indexCss).toContain('--info:');
  });

  it('defines every light colour token in the dark block', () => {
    const lightStart = indexCss.indexOf(':root {');
    const darkStart = indexCss.indexOf(":root[data-theme='dark']");
    const themeStart = indexCss.indexOf('@theme inline');

    const properties = (block: string) =>
      new Set(
        [...block.matchAll(/(--[a-z0-9-]+):/g)].map((match) => match[1]),
      );

    const light = properties(indexCss.slice(lightStart, darkStart));
    const dark = properties(indexCss.slice(darkStart, themeStart));
    const missing = [...light].filter(
      (token) => token !== '--radius' && !dark.has(token),
    );

    expect(missing).toEqual([]);
  });

  it('maps the palette to Tailwind utilities in the inline theme', () => {
    expect(indexCss).toContain('@theme inline {');
    expect(indexCss).toContain('--color-background: var(--background);');
    expect(indexCss).toContain('--color-success: var(--success);');
    expect(indexCss).toContain('--color-warning: var(--warning);');
    expect(indexCss).toContain('--color-info: var(--info);');
  });

  it('keeps the dark variant and the base layer', () => {
    expect(indexCss).toContain(
      '@custom-variant dark (&:where([data-theme=dark], [data-theme=dark] *));',
    );
    expect(indexCss).toContain('@layer base {');
    expect(indexCss).toContain('@apply bg-background text-foreground;');
    expect(indexCss).toContain('color: var(--primary);');
  });

  it('does not read or define the legacy colour aliases', () => {
    expect(indexCss).not.toMatch(/var\(--color-/);

    for (const legacyToken of [
      '--color-bg:',
      '--color-bg-subtle:',
      '--color-surface:',
      '--color-fg:',
      '--color-fg-muted:',
      '--color-danger:',
      '--color-code-bg:',
      '--color-stack-bg:',
      '--color-chip-bg:',
      '--color-row-selected:',
      '--color-frame-in-app-bg:',
      '--color-shadow:',
    ]) {
      expect(indexCss).not.toContain(legacyToken);
    }
  });

  it('does not carry the legacy global screen rules', () => {
    for (const legacyRule of [
      '--layout-max-width',
      '.page {',
      '.page-header',
      '.brand',
      '.top-bar',
      '.project-picker',
      '.tabs',
      '.tab {',
      'table {',
      'th,',
      'td {',
      'button {',
      'h1 {',
    ]) {
      expect(indexCss).not.toContain(legacyRule);
    }
  });
});
