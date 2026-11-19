import type { DeepStringify } from '../types';

/** English messages for the settings screen. */
export const settings = {
  title: 'Settings',
  description: 'Choose how the dashboard looks and which language it uses.',
  appearance: {
    title: 'Appearance',
    description: 'Pick the theme the dashboard uses. The change applies at once.',
    system: 'System',
    light: 'Light',
    dark: 'Dark',
  },
  language: {
    title: 'Language',
    description: 'Pick the language the dashboard uses. The change applies at once.',
    english: 'English',
    korean: '한국어',
  },
} as const;

export type SettingsMessages = DeepStringify<typeof settings>;
