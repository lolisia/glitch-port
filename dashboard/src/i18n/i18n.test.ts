import { act, renderHook } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { I18nProvider, useI18n } from './I18nProvider';
import {
  LOCALE_STORAGE_KEY,
  isLocale,
  localeFromLanguage,
  readStoredLocale,
  resolveLocale,
  writeStoredLocale,
} from './locale';
import { en, ko, type Messages } from './messages';
import { createTranslator, translate, type Dictionaries } from './translate';

/** Flattens a nested dictionary into dotted key → message pairs. */
function flattenMessages(
  value: unknown,
  prefix = '',
): Record<string, string> {
  const flattened: Record<string, string> = {};

  if (typeof value === 'string') {
    flattened[prefix] = value;
    return flattened;
  }

  if (typeof value === 'object' && value !== null) {
    for (const [key, child] of Object.entries(value)) {
      Object.assign(
        flattened,
        flattenMessages(child, prefix === '' ? key : `${prefix}.${key}`),
      );
    }
  }

  return flattened;
}

describe('messages', () => {
  it('defines the same key structure in English and Korean', () => {
    const english = flattenMessages(en);
    const korean = flattenMessages(ko);

    expect(Object.keys(english).length).toBeGreaterThan(0);
    expect(Object.keys(korean).sort()).toEqual(Object.keys(english).sort());
  });

  it('never leaves a message empty', () => {
    const values = [
      ...Object.values(flattenMessages(en)),
      ...Object.values(flattenMessages(ko)),
    ];

    expect(values.filter((value) => value.trim() === '')).toEqual([]);
  });

  it('translates the same dotted key to different text', () => {
    expect(createTranslator('en')('nav.issues')).toBe('Issues');
    expect(createTranslator('ko')('nav.issues')).toBe('이슈');
  });

  it('defines the issue, event, project and release namespaces in both locales', () => {
    const enT = createTranslator('en');
    const koT = createTranslator('ko');

    expect(enT('issues.title')).toBe('Issues');
    expect(koT('issues.title')).toBe('이슈');
    expect(enT('event.rawJson')).toBe('Raw JSON');
    expect(koT('event.rawJson')).toBe('원본 JSON');
    expect(enT('projects.title')).toBe('Projects');
    expect(koT('projects.title')).toBe('프로젝트');
    expect(enT('releases.title')).toBe('Releases');
    expect(koT('releases.title')).toBe('릴리스');
    expect(enT('nav.releases')).toBe('Releases');
    expect(koT('nav.releases')).toBe('릴리스');
  });

  it('defines the regression and tag-distribution messages in both locales', () => {
    const enT = createTranslator('en');
    const koT = createTranslator('ko');

    expect(enT('issues.filters.regressed')).toBe('Regressed');
    expect(koT('issues.filters.regressed')).toBe('재발');
    expect(enT('issues.detail.resolveNextRelease')).toBe(
      'Resolve in next release',
    );
    expect(koT('issues.detail.resolveNextRelease')).toBe(
      '다음 릴리스에서 해결',
    );
    expect(
      enT('issues.detail.reopenNote', { release: '1.2.0' }),
    ).toBe('This issue reopens if it recurs after 1.2.0.');
    expect(enT('issues.detail.tags.title')).toBe('Tag distribution');
    expect(koT('issues.detail.tags.title')).toBe('태그 분포');
    expect(enT('releases.table.newIssues')).toBe('New issues');
    expect(koT('releases.table.newIssues')).toBe('신규 이슈');
  });

  it('keeps the screen counters in plural keys', () => {
    const enT = createTranslator('en');
    const koT = createTranslator('ko');

    expect(enT('issues.stats.total', { count: 3 })).toBe('3 events');
    expect(enT('issues.stats.issueCount', { count: 1 })).toBe('1 issue');
    expect(enT('issues.stats.issueCount', { count: 3 })).toBe('3 issues');
    expect(enT('issues.bulk.selected', { count: 2 })).toBe('2 selected');
    expect(koT('issues.bulk.selected', { count: 2 })).toBe('2개 선택됨');
    expect(koT('issues.stats.issueCount', { count: 3 })).toBe('3개 이슈');
  });
});

describe('locale resolution', () => {
  it('maps a browser language starting with ko to Korean', () => {
    expect(localeFromLanguage('ko')).toBe('ko');
    expect(localeFromLanguage('ko-KR')).toBe('ko');
    expect(localeFromLanguage('KO-kr')).toBe('ko');
    expect(localeFromLanguage('en-US')).toBe('en');
    expect(localeFromLanguage('fr-FR')).toBe('en');
    expect(localeFromLanguage('')).toBe('en');
    expect(localeFromLanguage(undefined)).toBe('en');
    expect(localeFromLanguage(null)).toBe('en');
  });

  it('accepts only en and ko as stored locales', () => {
    expect(isLocale('en')).toBe(true);
    expect(isLocale('ko')).toBe(true);
    expect(isLocale('ko-KR')).toBe(false);
    expect(isLocale('fr')).toBe(false);
    expect(isLocale(null)).toBe(false);

    writeStoredLocale('ko');
    expect(window.localStorage.getItem(LOCALE_STORAGE_KEY)).toBe('ko');
    expect(readStoredLocale()).toBe('ko');

    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'fr');
    expect(readStoredLocale()).toBeNull();
  });

  it('prefers a valid stored locale over the browser language', () => {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'en');
    vi.stubGlobal('navigator', { language: 'ko-KR' });

    expect(resolveLocale()).toBe('en');
  });

  it('uses the browser language when nothing valid is stored', () => {
    vi.stubGlobal('navigator', { language: 'ko-KR' });

    expect(resolveLocale()).toBe('ko');

    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'not-a-locale');
    expect(resolveLocale()).toBe('ko');
  });

  it('defaults to English for an English browser', () => {
    vi.stubGlobal('navigator', { language: 'en-US' });

    expect(resolveLocale()).toBe('en');
  });
});

describe('I18nProvider', () => {
  it('resolves the stored locale and applies it to document.lang', () => {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'ko');

    const { result } = renderHook(() => useI18n(), { wrapper: I18nProvider });

    expect(result.current.locale).toBe('ko');
    expect(result.current.t('nav.messages')).toBe('메시지');
    expect(document.documentElement.lang).toBe('ko');
  });

  it('follows the browser language when nothing is stored', () => {
    vi.stubGlobal('navigator', { language: 'ko-KR' });

    const { result } = renderHook(() => useI18n(), { wrapper: I18nProvider });

    expect(result.current.locale).toBe('ko');
    expect(document.documentElement.lang).toBe('ko');
  });

  it('persists a locale change and updates document.lang', () => {
    const { result } = renderHook(() => useI18n(), { wrapper: I18nProvider });

    expect(result.current.locale).toBe('en');
    expect(result.current.t('nav.issues')).toBe('Issues');
    expect(document.documentElement.lang).toBe('en');

    act(() => {
      result.current.setLocale('ko');
    });

    expect(result.current.locale).toBe('ko');
    expect(result.current.t('nav.issues')).toBe('이슈');
    expect(window.localStorage.getItem(LOCALE_STORAGE_KEY)).toBe('ko');
    expect(document.documentElement.lang).toBe('ko');

    act(() => {
      result.current.setLocale('en');
    });

    expect(result.current.t('nav.issues')).toBe('Issues');
    expect(window.localStorage.getItem(LOCALE_STORAGE_KEY)).toBe('en');
    expect(document.documentElement.lang).toBe('en');
  });

  it('falls back to English outside a provider', () => {
    const { result } = renderHook(() => useI18n());

    expect(result.current.locale).toBe('en');
    expect(result.current.t('nav.projects')).toBe('Projects');
  });
});

describe('translate', () => {
  it('returns the key text for a missing key in every locale', () => {
    const enT = createTranslator('en');
    const koT = createTranslator('ko');

    expect(enT('nav.doesNotExist')).toBe('nav.doesNotExist');
    expect(koT('nav.doesNotExist')).toBe('nav.doesNotExist');
  });

  it('falls back to English when a Korean key is missing at runtime', () => {
    const partialKorean = Object.fromEntries(
      Object.entries(ko.messages).filter(([key]) => key !== 'empty'),
    );
    const dictionaries: Dictionaries = {
      en,
      ko: { ...ko, messages: partialKorean } as unknown as Messages,
    };

    expect(translate(dictionaries, 'ko', 'messages.empty')).toBe(
      'No messages yet.',
    );
    expect(translate(dictionaries, 'ko', 'messages.title')).toBe('메시지');
  });

  it('substitutes named placeholders', () => {
    const enT = createTranslator('en');
    const koT = createTranslator('ko');

    expect(enT('ui.tokenHint', { name: 'VITE_ADMIN_TOKEN' })).toBe(
      'Set VITE_ADMIN_TOKEN to an admin token accepted by the collector.',
    );
    expect(koT('ui.tokenHint', { name: 'VITE_ADMIN_TOKEN' })).toBe(
      'VITE_ADMIN_TOKEN에 collector가 허용하는 관리자 토큰을 설정하세요.',
    );
  });

  it('leaves unknown placeholders untouched', () => {
    expect(createTranslator('en')('ui.tokenHint')).toBe(
      'Set {name} to an admin token accepted by the collector.',
    );
  });

  it('selects the plural suffix with Intl.PluralRules', () => {
    const enT = createTranslator('en');

    expect(enT('ui.eventCount', { count: 1 })).toBe('1 event');
    expect(enT('ui.eventCount', { count: 2 })).toBe('2 events');
  });

  it('formats numeric values with Intl.NumberFormat', () => {
    expect(createTranslator('en')('ui.eventCount', { count: 1234 })).toBe(
      '1,234 events',
    );
  });

  it('uses the single Korean plural category', () => {
    const koT = createTranslator('ko');

    // Intl.PluralRules('ko') reports `other` for every count.
    expect(koT('ui.eventCount', { count: 1 })).toBe('1개 이벤트');
    expect(koT('ui.eventCount', { count: 3 })).toBe('3개 이벤트');
  });
});
