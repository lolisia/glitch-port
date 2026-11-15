/**
 * The mutable scope: tags, user, contexts and breadcrumbs that are attached to
 * every event captured by the same client.
 *
 * A scope is deliberately small and dependency free so it can run unchanged in
 * Deno (Supabase Edge Functions), Node and browsers. Capturing an event goes
 * through {@link Scope.snapshot}, which returns copies: later scope mutations
 * never change an event that has already been built.
 */

import { DEFAULT_MAX_BREADCRUMBS, MAX_BREADCRUMBS, MAX_TAGS } from './limits.ts';
import type {
  GlitchPortBreadcrumb,
  GlitchPortBreadcrumbInput,
  GlitchPortUser,
  ScopeSnapshot,
} from './types.ts';

/** Options accepted by {@link Scope}. */
export interface ScopeOptions {
  /**
   * Maximum number of breadcrumbs retained. Defaults to
   * `DEFAULT_MAX_BREADCRUMBS` (50) and is clamped to
   * 0..`MAX_BREADCRUMBS` (0..100).
   */
  maxBreadcrumbs?: number;
}

/**
 * Normalize a configured breadcrumb capacity into the supported range.
 * Missing and non-finite values fall back to the default.
 */
function normalizeMaxBreadcrumbs(value: number | undefined): number {
  if (value === undefined || !Number.isFinite(value)) {
    return DEFAULT_MAX_BREADCRUMBS;
  }
  return Math.min(MAX_BREADCRUMBS, Math.max(0, Math.floor(value)));
}

/** Shallow-copy a breadcrumb, including its `data` bag. */
function copyBreadcrumb(breadcrumb: GlitchPortBreadcrumb): GlitchPortBreadcrumb {
  const copy: GlitchPortBreadcrumb = { ...breadcrumb };
  if (breadcrumb.data !== undefined) {
    copy.data = { ...breadcrumb.data };
  }
  return copy;
}

/** Keep the most recent `limit` breadcrumbs, as copies. */
function lastBreadcrumbs(
  breadcrumbs: readonly GlitchPortBreadcrumb[],
  limit: number,
): GlitchPortBreadcrumb[] {
  if (limit <= 0 || breadcrumbs.length === 0) {
    return [];
  }
  return breadcrumbs.slice(-limit).map(copyBreadcrumb);
}

/**
 * Mutable collection of the state attached to every captured event.
 *
 * Tags are capped at `MAX_TAGS` keys (the oldest key is dropped when a new one
 * would exceed the cap), breadcrumbs are capped at `maxBreadcrumbs` (default
 * 50, hard cap 100) and contexts are removed by setting them to `null`.
 */
export class Scope {
  private readonly tags = new Map<string, string>();
  private readonly contexts = new Map<string, unknown>();
  private breadcrumbs: GlitchPortBreadcrumb[];
  private user: GlitchPortUser | undefined;
  private readonly maxBreadcrumbs: number;

  constructor(initial: Partial<ScopeSnapshot> = {}, options: ScopeOptions = {}) {
    this.maxBreadcrumbs = normalizeMaxBreadcrumbs(options.maxBreadcrumbs);
    this.breadcrumbs = [];

    if (initial.tags !== undefined) {
      for (const [key, value] of Object.entries(initial.tags)) {
        this.setTag(key, value);
      }
    }

    if (initial.user !== undefined) {
      this.setUser(initial.user);
    }

    if (initial.contexts !== undefined) {
      for (const [name, context] of Object.entries(initial.contexts)) {
        this.contexts.set(name, context);
      }
    }

    if (initial.breadcrumbs !== undefined) {
      this.breadcrumbs = lastBreadcrumbs(initial.breadcrumbs, this.maxBreadcrumbs);
    }
  }

  /**
   * Set a tag. When the scope already holds `MAX_TAGS` distinct keys, the
   * oldest key is dropped so the newest tag always fits. Updating an existing
   * key never drops another tag.
   */
  setTag(key: string, value: string): void {
    if (!this.tags.has(key) && this.tags.size >= MAX_TAGS) {
      const oldest = this.tags.keys().next();
      if (!oldest.done) {
        this.tags.delete(oldest.value);
      }
    }
    this.tags.set(key, value);
  }

  /** Attach the current user, or clear it with `null`. */
  setUser(user: GlitchPortUser | null): void {
    this.user = user === null ? undefined : { ...user };
  }

  /** Attach a named context, or remove it with `null`. */
  setContext(name: string, context: Record<string, unknown> | null): void {
    if (context === null) {
      this.contexts.delete(name);
      return;
    }
    this.contexts.set(name, { ...context });
  }

  /**
   * Record a breadcrumb. Missing fields are filled in (`timestamp` with the
   * current ISO time, `level` with `info`) and only the most recent
   * `maxBreadcrumbs` entries are retained.
   */
  addBreadcrumb(breadcrumb: GlitchPortBreadcrumbInput): void {
    if (this.maxBreadcrumbs === 0) {
      return;
    }

    const providedTimestamp = (
      breadcrumb as GlitchPortBreadcrumbInput & { timestamp?: string }
    ).timestamp;
    const normalized: GlitchPortBreadcrumb = {
      ...breadcrumb,
      level: breadcrumb.level ?? 'info',
      timestamp: providedTimestamp ?? new Date().toISOString(),
    };
    if (breadcrumb.data !== undefined) {
      normalized.data = { ...breadcrumb.data };
    }

    this.breadcrumbs.push(normalized);
    if (this.breadcrumbs.length > this.maxBreadcrumbs) {
      this.breadcrumbs.splice(0, this.breadcrumbs.length - this.maxBreadcrumbs);
    }
  }

  /**
   * Copy the scope. The clone preserves `maxBreadcrumbs` and owns every piece
   * of mutable state, so changes to the clone never affect this scope.
   */
  clone(): Scope {
    return new Scope(this.snapshot(), { maxBreadcrumbs: this.maxBreadcrumbs });
  }

  /** Return an independent snapshot of the current scope. */
  snapshot(): ScopeSnapshot {
    const snapshot: ScopeSnapshot = {
      tags: Object.fromEntries(this.tags),
      contexts: Object.fromEntries(this.contexts),
      breadcrumbs: this.breadcrumbs.map(copyBreadcrumb),
    };
    if (this.user !== undefined) {
      snapshot.user = { ...this.user };
    }
    return snapshot;
  }
}
