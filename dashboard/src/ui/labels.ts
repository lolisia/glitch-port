import type { Translate } from '../i18n';

/**
 * Looks up a label and keeps `fallback` when the message is missing or only
 * differs from the raw value by case.
 *
 * The English level/status labels mirror the API values, so this preserves the
 * pre-i18n spelling (including mixed-case input such as `Error`) instead of
 * normalising it, while other locales get their translated label.
 */
export function translatedLabel(
  t: Translate,
  key: string,
  fallback: string,
): string {
  const translated = t(key);

  if (translated === key) {
    return fallback;
  }

  return translated.toLowerCase() === fallback.toLowerCase()
    ? fallback
    : translated;
}
