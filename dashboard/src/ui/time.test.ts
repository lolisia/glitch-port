import { describe, expect, it } from 'vitest';
import {
  formatAbsoluteTime,
  formatClockTime,
  formatRelativeTime,
} from './time';

const now = new Date(2026, 0, 15, 12, 0, 0);

function secondsAgo(seconds: number): string {
  return new Date(now.getTime() - seconds * 1000).toISOString();
}

describe('formatAbsoluteTime', () => {
  it('formats local time with zero padding', () => {
    const value = new Date(2026, 0, 2, 3, 4, 5).toISOString();
    expect(formatAbsoluteTime(value)).toBe('2026-01-02 03:04:05');
  });

  it('returns invalid input unchanged', () => {
    expect(formatAbsoluteTime('not-a-date')).toBe('not-a-date');
  });
});

describe('formatClockTime', () => {
  it('formats the local time of day', () => {
    const value = new Date(2026, 0, 2, 3, 4, 5).toISOString();
    expect(formatClockTime(value)).toBe('03:04:05');
  });

  it('returns invalid input unchanged', () => {
    expect(formatClockTime('not-a-date')).toBe('not-a-date');
  });
});

describe('formatRelativeTime', () => {
  it('reports sub-minute and future values as just now', () => {
    expect(formatRelativeTime(secondsAgo(0), now)).toBe('just now');
    expect(formatRelativeTime(secondsAgo(59), now)).toBe('just now');
    expect(formatRelativeTime(secondsAgo(-3600), now)).toBe('just now');
  });

  it.each([
    [60, '1 minute ago'],
    [120, '2 minutes ago'],
    [3599, '59 minutes ago'],
    [3600, '1 hour ago'],
    [7200, '2 hours ago'],
    [86400, '1 day ago'],
    [2 * 86400, '2 days ago'],
    [29 * 86400, '29 days ago'],
  ])('formats %i seconds as %s', (seconds, expected) => {
    expect(formatRelativeTime(secondsAgo(seconds), now)).toBe(expected);
  });

  it('falls back to the absolute time from 30 days', () => {
    const older = secondsAgo(30 * 86400);
    expect(formatRelativeTime(older, now)).toBe(formatAbsoluteTime(older));
  });

  it('returns invalid input unchanged', () => {
    expect(formatRelativeTime('not-a-date', now)).toBe('not-a-date');
  });
});
