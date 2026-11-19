import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it } from 'vitest';
import { I18nProvider, LOCALE_STORAGE_KEY } from '../../i18n';
import { ThemeProvider } from '../../theme/ThemeProvider';
import { THEME_STORAGE_KEY } from '../../theme/theme';
import { SettingsPage } from './SettingsPage';

function renderSettings() {
  return render(
    <ThemeProvider>
      <I18nProvider>
        <SettingsPage />
      </I18nProvider>
    </ThemeProvider>,
  );
}

function optionLabels(select: HTMLElement): string[] {
  return Array.from(select.querySelectorAll('option')).map(
    (option) => option.textContent ?? '',
  );
}

beforeEach(() => {
  window.history.replaceState(null, '', '/');
  window.localStorage.clear();
  document.documentElement.removeAttribute('data-theme');
  document.documentElement.style.removeProperty('color-scheme');
  document.documentElement.lang = 'en';
});

describe('SettingsPage', () => {
  it('renders the appearance and language cards', () => {
    renderSettings();

    expect(screen.getByTestId('settings-page')).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { level: 2, name: 'Settings' }),
    ).toBeInTheDocument();
    expect(screen.getByText('Appearance')).toBeInTheDocument();
    expect(screen.getByText('Language')).toBeInTheDocument();

    const appearance = screen.getByRole('radiogroup', { name: 'Theme' });
    expect(
      within(appearance).getByRole('radio', { name: 'System' }),
    ).toBeChecked();
    expect(
      within(appearance).getByRole('radio', { name: 'Light' }),
    ).not.toBeChecked();
    expect(
      within(appearance).getByRole('radio', { name: 'Dark' }),
    ).not.toBeChecked();

    const language = screen.getByLabelText('Language');
    expect(language).toHaveValue('en');
    expect(optionLabels(language)).toEqual(['English', '한국어']);
  });

  it('applies and persists a theme radio click immediately', async () => {
    const user = userEvent.setup();
    renderSettings();

    const appearance = screen.getByRole('radiogroup', { name: 'Theme' });
    const dark = within(appearance).getByRole('radio', { name: 'Dark' });

    await user.click(dark);

    expect(dark).toBeChecked();
    expect(document.documentElement.dataset.theme).toBe('dark');
    expect(document.documentElement.style.colorScheme).toBe('dark');
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark');

    await user.click(
      within(appearance).getByRole('radio', { name: 'Light' }),
    );

    expect(document.documentElement.dataset.theme).toBe('light');
    expect(document.documentElement.style.colorScheme).toBe('light');
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('light');
  });

  it('applies and persists a language change and keeps the Language label', async () => {
    const user = userEvent.setup();
    renderSettings();

    expect(document.documentElement.lang).toBe('en');

    await user.selectOptions(screen.getByLabelText('Language'), 'ko');

    // The whole screen re-renders in Korean, including the cards and the radio
    // group label, while the fixed Language label stays in English.
    expect(
      screen.getByRole('heading', { level: 2, name: '설정' }),
    ).toBeInTheDocument();
    expect(screen.getByText('화면')).toBeInTheDocument();
    expect(screen.getByRole('radiogroup', { name: '테마' })).toBeInTheDocument();
    expect(
      screen.getByRole('radio', { name: '시스템' }),
    ).toBeInTheDocument();

    const language = screen.getByLabelText('Language');
    expect(language).toHaveValue('ko');
    expect(optionLabels(language)).toEqual(['English', '한국어']);
    expect(document.documentElement.lang).toBe('ko');
    expect(window.localStorage.getItem(LOCALE_STORAGE_KEY)).toBe('ko');
  });

  it('resolves stored preferences on mount', () => {
    window.localStorage.setItem(THEME_STORAGE_KEY, 'dark');
    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'ko');

    renderSettings();

    const appearance = screen.getByRole('radiogroup', { name: '테마' });
    expect(
      within(appearance).getByRole('radio', { name: '다크' }),
    ).toBeChecked();
    expect(document.documentElement.dataset.theme).toBe('dark');
    expect(screen.getByLabelText('Language')).toHaveValue('ko');
    expect(document.documentElement.lang).toBe('ko');
  });
});
