import type { Locale } from './locale';
import { messagesByLocale, type Messages } from './messages';

/** Values substituted into `{placeholder}` tokens. */
export type TranslationParams = Record<string, string | number>;

/** The dictionary for every supported locale. */
export type Dictionaries = Record<Locale, Messages>;

export type Translate = (key: string, params?: TranslationParams) => string;

// Formatters are comparatively expensive to construct, so keep one per locale.
const pluralRules = new Map<Locale, Intl.PluralRules>();
const numberFormats = new Map<Locale, Intl.NumberFormat>();

function pluralRulesFor(locale: Locale): Intl.PluralRules {
  let rules = pluralRules.get(locale);

  if (rules === undefined) {
    rules = new Intl.PluralRules(locale);
    pluralRules.set(locale, rules);
  }

  return rules;
}

function numberFormatFor(locale: Locale): Intl.NumberFormat {
  let format = numberFormats.get(locale);

  if (format === undefined) {
    format = new Intl.NumberFormat(locale);
    numberFormats.set(locale, format);
  }

  return format;
}

/** Reads a dotted key, returning `undefined` for a missing or non-string leaf. */
function readTemplate(
  dictionary: Messages,
  key: string,
): string | undefined {
  let current: unknown = dictionary;

  for (const segment of key.split('.')) {
    if (typeof current !== 'object' || current === null) {
      return undefined;
    }

    current = (current as Record<string, unknown>)[segment];
  }

  return typeof current === 'string' ? current : undefined;
}

/**
 * Picks the template for a key. With a numeric `count` the `_one`/`_other`
 * plural variants take precedence (the category comes from
 * `Intl.PluralRules`); otherwise the exact key is used. Missing keys fall back
 * to English, and an entirely unknown key resolves to `undefined`.
 */
function resolveTemplate(
  dictionaries: Dictionaries,
  locale: Locale,
  key: string,
  params: TranslationParams | undefined,
): string | undefined {
  const category =
    typeof params?.count === 'number'
      ? pluralRulesFor(locale).select(params.count)
      : null;

  for (const dictionary of [dictionaries[locale], dictionaries.en]) {
    if (category !== null) {
      const plural =
        readTemplate(dictionary, `${key}_${category}`) ??
        readTemplate(dictionary, `${key}_other`);

      if (plural !== undefined) {
        return plural;
      }
    }

    const base = readTemplate(dictionary, key);

    if (base !== undefined) {
      return base;
    }
  }

  return undefined;
}

/**
 * Replaces `{name}` tokens. Numbers are formatted with `Intl.NumberFormat` for
 * the active locale (formatted counts, for example `1,234`), so a numeric
 * `count` used for plural selection is also the value rendered in the text.
 * Unknown placeholders are left untouched instead of rendering `undefined`.
 */
function substitute(
  template: string,
  params: TranslationParams | undefined,
  locale: Locale,
): string {
  if (params === undefined) {
    return template;
  }

  return template.replace(/\{(\w+)\}/g, (placeholder, name: string) => {
    const value = params[name];

    if (value === undefined) {
      return placeholder;
    }

    return typeof value === 'number'
      ? numberFormatFor(locale).format(value)
      : value;
  });
}

/**
 * Resolves a dotted message key for one locale. The fallback chain is the
 * active locale, then English, then the key itself.
 */
export function translate(
  dictionaries: Dictionaries,
  locale: Locale,
  key: string,
  params?: TranslationParams,
): string {
  const template = resolveTemplate(dictionaries, locale, key, params);

  if (template === undefined) {
    return key;
  }

  return substitute(template, params, locale);
}

/** Builds the `t(key, params?)` function bound to a locale. */
export function createTranslator(
  locale: Locale,
  dictionaries: Dictionaries = messagesByLocale,
): Translate {
  return (key, params) => translate(dictionaries, locale, key, params);
}

/**
 * Formats a standalone number (a count shown in its own cell, for example)
 * with the active locale's `Intl.NumberFormat`. Use `t()` with a `{count}`
 * placeholder instead when the number is part of a sentence.
 */
export function formatNumber(value: number, locale: Locale): string {
  return numberFormatFor(locale).format(value);
}
