import type { Locale } from '../i18n';

const MINUTE_SECONDS = 60;
const HOUR_SECONDS = 60 * MINUTE_SECONDS;
const DAY_SECONDS = 24 * HOUR_SECONDS;
const RELATIVE_DAY_LIMIT = 30;

/**
 * Date separators for the locale-specific absolute timestamp. English keeps
 * the pre-i18n `YYYY-MM-DD HH:mm:ss` display; Korean follows its
 * `YYYY. MM. DD. HH:mm:ss` convention. The numbers themselves come from
 * `Intl.DateTimeFormat` for the active locale.
 */
const DATE_LAYOUT: Record<Locale, { separator: string; suffix: string }> = {
  en: { separator: '-', suffix: '' },
  ko: { separator: '. ', suffix: '.' },
};

const dateTimeFormatters = new Map<Locale, Intl.DateTimeFormat>();
const relativeFormatters = new Map<Locale, Intl.RelativeTimeFormat>();

function dateTimeFormatter(locale: Locale): Intl.DateTimeFormat {
  let formatter = dateTimeFormatters.get(locale);

  if (formatter === undefined) {
    formatter = new Intl.DateTimeFormat(locale, {
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
      // h23 keeps midnight as 00:xx instead of the h24 24:xx.
      hourCycle: 'h23',
    });
    dateTimeFormatters.set(locale, formatter);
  }

  return formatter;
}

function relativeFormatter(locale: Locale): Intl.RelativeTimeFormat {
  let formatter = relativeFormatters.get(locale);

  if (formatter === undefined) {
    // `numeric: 'always'` keeps "1 day ago" instead of "yesterday", so English
    // output stays identical to the pre-i18n strings.
    formatter = new Intl.RelativeTimeFormat(locale, {
      numeric: 'always',
      style: 'long',
    });
    relativeFormatters.set(locale, formatter);
  }

  return formatter;
}

function parseDate(value: string): Date | null {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

interface DateTimeFields {
  year: string;
  month: string;
  day: string;
  hour: string;
  minute: string;
  second: string;
}

function dateTimeFields(date: Date, locale: Locale): DateTimeFields {
  const values: Record<string, string> = {};

  for (const part of dateTimeFormatter(locale).formatToParts(date)) {
    if (part.type !== 'literal') {
      values[part.type] = part.value;
    }
  }

  return {
    year: values.year ?? '',
    month: values.month ?? '',
    day: values.day ?? '',
    hour: values.hour ?? '',
    minute: values.minute ?? '',
    second: values.second ?? '',
  };
}

function formatClock(fields: DateTimeFields): string {
  return `${fields.hour}:${fields.minute}:${fields.second}`;
}

/**
 * Local `YYYY-MM-DD HH:mm:ss` for English and `YYYY. MM. DD. HH:mm:ss` for
 * Korean, using the active locale's `Intl.DateTimeFormat` numbers. Invalid
 * input is returned unchanged.
 */
export function formatAbsoluteTime(
  value: string,
  locale: Locale = 'en',
): string {
  const date = parseDate(value);

  if (date === null) {
    return value;
  }

  const fields = dateTimeFields(date, locale);
  const layout = DATE_LAYOUT[locale];
  const datePart =
    `${fields.year}${layout.separator}${fields.month}` +
    `${layout.separator}${fields.day}${layout.suffix}`;

  return `${datePart} ${formatClock(fields)}`;
}

/** Local 24-hour `HH:mm:ss`; invalid input is returned unchanged. */
export function formatClockTime(
  value: string,
  locale: Locale = 'en',
): string {
  const date = parseDate(value);

  if (date === null) {
    return value;
  }

  return formatClock(dateTimeFields(date, locale));
}

export interface FormatRelativeTimeOptions {
  /** Active UI locale; defaults to English. */
  locale?: Locale;
  /** Reference time, injectable for tests. */
  now?: Date;
  /**
   * Localized label for values under a minute. `RelativeTime` passes the
   * dictionary string so Korean users see "방금 전" instead of English.
   */
  justNow?: string;
}

function formatRelativeValue(
  value: number,
  unit: 'minute' | 'hour' | 'day',
  locale: Locale,
): string {
  // RelativeTimeFormat expects negative values for the past.
  return relativeFormatter(locale).format(-value, unit);
}

/**
 * Compact relative time through `Intl.RelativeTimeFormat`. Values less than a
 * minute old (including clock skew that puts them in the future) read "just
 * now"; anything at or beyond 30 days falls back to the absolute local
 * timestamp in the active locale.
 */
export function formatRelativeTime(
  value: string,
  options: FormatRelativeTimeOptions = {},
): string {
  const { locale = 'en', now = new Date(), justNow = 'just now' } = options;
  const date = parseDate(value);

  if (date === null) {
    return formatAbsoluteTime(value, locale);
  }

  const seconds = Math.floor((now.getTime() - date.getTime()) / 1000);

  if (seconds < MINUTE_SECONDS) {
    return justNow;
  }

  if (seconds < HOUR_SECONDS) {
    return formatRelativeValue(
      Math.floor(seconds / MINUTE_SECONDS),
      'minute',
      locale,
    );
  }

  if (seconds < DAY_SECONDS) {
    return formatRelativeValue(
      Math.floor(seconds / HOUR_SECONDS),
      'hour',
      locale,
    );
  }

  const days = Math.floor(seconds / DAY_SECONDS);
  if (days < RELATIVE_DAY_LIMIT) {
    return formatRelativeValue(days, 'day', locale);
  }

  return formatAbsoluteTime(value, locale);
}
