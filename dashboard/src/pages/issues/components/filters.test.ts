import { describe, expect, it } from 'vitest';
import {
  DEFAULT_ISSUE_FILTERS,
  buildIssueFilterParams,
  buildIssueListQuery,
  parsePlatformFilter,
  parseRegressedFilter,
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

describe('regression filters', () => {
  it('parses the regressed flag as enabled only for true', () => {
    expect(parseRegressedFilter('true')).toBe(true);
    expect(parseRegressedFilter(' TRUE ')).toBe(true);
    expect(parseRegressedFilter('false')).toBe(false);
    expect(parseRegressedFilter('')).toBe(false);
    expect(parseRegressedFilter(null)).toBe(false);
  });

  it('reads first_release and regressed from the hash query', () => {
    const query = new URLSearchParams(
      'first_release=game%401.0.0&regressed=true',
    );
    const filters = readIssueFilters(query);

    expect(filters.first_release).toBe('game@1.0.0');
    expect(filters.regressed).toBe(true);

    const defaults = readIssueFilters(new URLSearchParams());
    expect(defaults.first_release).toBe('');
    expect(defaults.regressed).toBe(false);
  });

  it('writes regressed=true only when enabled and first_release when non-empty', () => {
    const params = buildIssueFilterParams({
      ...DEFAULT_ISSUE_FILTERS,
      first_release: 'game@1.0.0',
      regressed: true,
    });

    expect(params.get('first_release')).toBe('game@1.0.0');
    expect(params.getAll('regressed')).toEqual(['true']);

    const disabled = buildIssueFilterParams({
      ...DEFAULT_ISSUE_FILTERS,
      regressed: false,
    });
    expect(disabled.has('regressed')).toBe(false);
    expect(disabled.has('first_release')).toBe(false);
    expect(disabled.toString()).toBe('');

    const blank = buildIssueFilterParams({
      ...DEFAULT_ISSUE_FILTERS,
      first_release: '',
      regressed: true,
    });
    expect(blank.has('first_release')).toBe(false);
    expect(blank.getAll('regressed')).toEqual(['true']);
  });

  it('passes the new filters to the list request only when they are set', () => {
    const query = buildIssueListQuery({
      ...DEFAULT_ISSUE_FILTERS,
      first_release: 'game@1.0.0',
      regressed: true,
    });

    expect(query.first_release).toBe('game@1.0.0');
    expect(query.regressed).toBe(true);

    const defaults = buildIssueListQuery(DEFAULT_ISSUE_FILTERS);
    expect(defaults.first_release).toBe('');
    expect(defaults).not.toHaveProperty('regressed');
  });

  it('round-trips the new filters through URL params and back', () => {
    const params = buildIssueFilterParams({
      ...DEFAULT_ISSUE_FILTERS,
      first_release: 'game@1.0.0',
      regressed: true,
    });
    const filters = readIssueFilters(params);

    expect(filters.first_release).toBe('game@1.0.0');
    expect(filters.regressed).toBe(true);
  });
});
