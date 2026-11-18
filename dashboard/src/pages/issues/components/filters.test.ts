import { describe, expect, it } from 'vitest';
import {
  DEFAULT_ISSUE_FILTERS,
  buildIssueFilterParams,
  buildIssueListQuery,
  parsePlatformFilter,
  readIssueFilters,
} from './filters';

describe('platform filter', () => {
  it('parses a comma-separated value into distinct trimmed platforms', () => {
    expect(parsePlatformFilter('unity, dotnet,,unreal ')).toEqual([
      'unity',
      'dotnet',
      'unreal',
    ]);
  });

  it('treats a missing, blank or all-comma value as no selection', () => {
    expect(parsePlatformFilter(null)).toEqual([]);
    expect(parsePlatformFilter('')).toEqual([]);
    expect(parsePlatformFilter(' , , ')).toEqual([]);
  });

  it('reads the platform key from the hash query', () => {
    const query = new URLSearchParams('platform=unity%2Cdotnet&level=error');

    expect(readIssueFilters(query)).toMatchObject({
      level: 'error',
      platform: ['unity', 'dotnet'],
    });
    expect(readIssueFilters(new URLSearchParams()).platform).toEqual([]);
  });

  it('writes one joined platform parameter and omits an empty selection', () => {
    const params = buildIssueFilterParams({
      ...DEFAULT_ISSUE_FILTERS,
      platform: ['unity', 'dotnet'],
    });

    expect(params.getAll('platform')).toEqual(['unity,dotnet']);
    expect(params.toString()).toBe('platform=unity%2Cdotnet');

    expect(
      buildIssueFilterParams({
        ...DEFAULT_ISSUE_FILTERS,
        platform: [],
      }).has('platform'),
    ).toBe(false);
  });

  it('passes the platform array to the list request', () => {
    expect(
      buildIssueListQuery({
        ...DEFAULT_ISSUE_FILTERS,
        platform: ['unity'],
      }).platform,
    ).toEqual(['unity']);

    expect(buildIssueListQuery(DEFAULT_ISSUE_FILTERS).platform).toEqual([]);
  });

  it('round-trips the filter through URL params and back', () => {
    const params = buildIssueFilterParams({
      ...DEFAULT_ISSUE_FILTERS,
      platform: ['unity', 'dotnet'],
    });

    expect(readIssueFilters(params).platform).toEqual(['unity', 'dotnet']);
  });
});
