import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react';
import {
  DEFAULT_LOCALE,
  resolveLocale,
  writeStoredLocale,
  type Locale,
} from './locale';
import { createTranslator, type Translate } from './translate';

export interface I18nContextValue {
  locale: Locale;
  setLocale: (locale: Locale) => void;
  /** Resolves a dotted key with `{param}` substitution and plural selection. */
  t: Translate;
}

/**
 * Outside the provider the hook keeps rendering the English source strings.
 * Standalone component tests (and the UI primitives) therefore keep working
 * without an explicit wrapper; an actual locale switch needs the provider.
 */
const fallbackValue: I18nContextValue = {
  locale: DEFAULT_LOCALE,
  setLocale: () => {},
  t: createTranslator(DEFAULT_LOCALE),
};

const I18nContext = createContext<I18nContextValue>(fallbackValue);

/**
 * Owns the active locale. The initial value is the stored preference, then the
 * browser language, then English. An explicit choice is persisted under
 * `glitchport.locale`, and `document.documentElement.lang` follows the active
 * locale so assistive technology and the browser pick the right language.
 */
export function I18nProvider({ children }: { children: ReactNode }) {
  const [locale, setLocaleState] = useState<Locale>(resolveLocale);

  const setLocale = useCallback((next: Locale) => {
    setLocaleState(next);
    writeStoredLocale(next);
  }, []);

  useEffect(() => {
    document.documentElement.lang = locale;
  }, [locale]);

  const value = useMemo<I18nContextValue>(
    () => ({ locale, setLocale, t: createTranslator(locale) }),
    [locale, setLocale],
  );

  return <I18nContext.Provider value={value}>{children}</I18nContext.Provider>;
}

export function useI18n(): I18nContextValue {
  return useContext(I18nContext);
}
