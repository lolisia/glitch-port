/**
 * English source messages. `as const` keeps the literal values so the
 * {@link Messages} type describes the exact key structure every other locale
 * must provide; a missing key in `ko.ts` is a compile error.
 *
 * Keys are resolved with dotted paths (`nav.issues`). A `{name}` placeholder is
 * replaced with the matching `t(key, params)` value, and keys with a `_one` /
 * `_other` suffix support `Intl.PluralRules` selection when `params.count` is a
 * number.
 */

import { event } from './en/event';
import { issues } from './en/issues';
import { projects } from './en/projects';
import { releases } from './en/releases';
import type { DeepStringify } from './types';

export const en = {
  nav: {
    sections: 'Sections',
    issues: 'Issues',
    releases: 'Releases',
    messages: 'Messages',
    projects: 'Projects',
  },
  common: {
    language: 'Language',
    theme: 'Theme',
    themeSystem: 'System',
    themeLight: 'Light',
    themeDark: 'Dark',
    project: 'Project',
    refresh: 'Refresh',
    loading: 'Loading...',
  },
  app: {
    projectsLoading: 'Loading projects...',
    projectsUnavailable: 'Projects unavailable.',
    noProjects: 'No projects yet',
    createOne: 'create one',
  },
  ui: {
    level: {
      debug: 'debug',
      info: 'info',
      warning: 'warning',
      error: 'error',
      fatal: 'fatal',
      trace: 'trace',
      unknown: 'unknown',
    },
    status: {
      unresolved: 'Unresolved',
      resolved: 'Resolved',
      ignored: 'Ignored',
    },
    justNow: 'just now',
    // The environment variable name is a parameter so the sentence can be
    // reordered by a translation without moving the identifier.
    tokenHint: 'Set {name} to an admin token accepted by the collector.',
    // Foundation for count summaries; `count` is formatted with
    // Intl.NumberFormat and drives the plural selection.
    eventCount_one: '{count} event',
    eventCount_other: '{count} events',
  },
  messages: {
    title: 'Messages',
    receivedAt: 'Received at',
    endpoint: 'Endpoint',
    message: 'Message',
    empty: 'No messages yet.',
    loadError: 'Failed to load messages',
  },
  issues,
  event,
  projects,
  releases,
} as const;

export type Messages = DeepStringify<typeof en>;
