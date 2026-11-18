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
  applyTheme,
  readThemePreference,
  resolveTheme,
  systemPrefersDark,
  watchSystemTheme,
  writeThemePreference,
  type ResolvedTheme,
  type ThemePreference,
} from './theme';

export interface ThemeContextValue {
  preference: ThemePreference;
  resolvedTheme: ResolvedTheme;
  setPreference: (preference: ThemePreference) => void;
}

const ThemeContext = createContext<ThemeContextValue | null>(null);

/**
 * Owns the theme preference, resolves `system` against the OS setting and
 * keeps `data-theme`/`color-scheme` plus the stored preference in sync.
 */
export function ThemeProvider({ children }: { children: ReactNode }) {
  const [preference, setPreferenceState] = useState<ThemePreference>(
    readThemePreference,
  );
  const [prefersDark, setPrefersDark] = useState<boolean>(systemPrefersDark);

  const resolvedTheme = resolveTheme(preference, prefersDark);

  // Apply on mount and whenever the resolved theme changes.
  useEffect(() => {
    applyTheme(resolvedTheme);
  }, [resolvedTheme]);

  // Persist on mount and whenever the preference changes.
  useEffect(() => {
    writeThemePreference(preference);
  }, [preference]);

  // Only follow the media query while the preference is `system`; switching to
  // an explicit theme unsubscribes on cleanup.
  useEffect(() => {
    if (preference !== 'system') {
      return undefined;
    }

    // The preference may have changed back to `system` after the initial
    // render, so adopt the current OS setting instead of a stale one.
    setPrefersDark(systemPrefersDark());

    return watchSystemTheme(setPrefersDark);
  }, [preference]);

  const setPreference = useCallback((next: ThemePreference) => {
    setPreferenceState(next);
  }, []);

  const value = useMemo<ThemeContextValue>(
    () => ({ preference, resolvedTheme, setPreference }),
    [preference, resolvedTheme, setPreference],
  );

  return (
    <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>
  );
}

export function useTheme(): ThemeContextValue {
  const value = useContext(ThemeContext);

  if (value === null) {
    throw new Error('useTheme must be used inside a ThemeProvider.');
  }

  return value;
}
