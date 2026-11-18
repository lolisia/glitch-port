/**
 * Locale selection shared by the provider, the top-bar language control and
 * the persisted preference.
 *
 * The dashboard ships two locales: English (the source strings) and Korean.
 * The browser language only picks the initial value; an explicit choice in the
 * top bar is stored under {@link LOCALE_STORAGE_KEY} and wins on later visits.
 */

export type Locale = 'en' | 'ko';

export const LOCALE_STORAGE_KEY = 'glitchport.locale';

export const DEFAULT_LOCALE: Locale = 'en';

export function isLocale(value: unknown): value is Locale {
  return value === 'en' || value === 'ko';
}

/**
 * A browser language starting with `ko` (for example `ko`, `ko-KR`) selects
 * Korean; every other language falls back to English, the source locale.
 */
export function localeFromLanguage(
  language: string | null | undefined,
): Locale {
  return typeof language === 'string' &&
    language.toLowerCase().startsWith('ko')
    ? 'ko'
    : DEFAULT_LOCALE;
}

/** The valid stored locale, or `null` when it is missing or invalid. */
export function readStoredLocale(): Locale | null {
  if (typeof window === 'undefined') {
    return null;
  }

  try {
    const stored = window.localStorage.getItem(LOCALE_STORAGE_KEY);
    return isLocale(stored) ? stored : null;
  } catch {
    // localStorage can be unavailable (privacy mode, disabled cookies).
    return null;
  }
}

/** Persists an explicit choice; failures leave the session-only value. */
export function writeStoredLocale(locale: Locale): void {
  if (typeof window === 'undefined') {
    return;
  }

  try {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, locale);
  } catch {
    // Ignore unavailable storage.
  }
}

/**
 * Valid stored locale, then the browser language, then English. This is the
 * order the provider resolves on mount.
 */
export function resolveLocale(): Locale {
  const stored = readStoredLocale();

  if (stored !== null) {
    return stored;
  }

  return localeFromLanguage(
    typeof navigator === 'undefined' ? undefined : navigator.language,
  );
}
