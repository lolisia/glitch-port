const MINUTE_SECONDS = 60;
const HOUR_SECONDS = 60 * MINUTE_SECONDS;
const DAY_SECONDS = 24 * HOUR_SECONDS;
const RELATIVE_DAY_LIMIT = 30;

function pad(part: number): string {
  return String(part).padStart(2, '0');
}

function parseDate(value: string): Date | null {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

/** Local YYYY-MM-DD HH:mm:ss; invalid input is returned unchanged. */
export function formatAbsoluteTime(value: string): string {
  const date = parseDate(value);

  if (date === null) {
    return value;
  }

  return (
    `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ` +
    `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`
  );
}

/** Local HH:mm:ss; invalid input is returned unchanged. */
export function formatClockTime(value: string): string {
  const date = parseDate(value);

  if (date === null) {
    return value;
  }

  return `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(
    date.getSeconds(),
  )}`;
}

function plural(count: number, unit: string): string {
  return `${count} ${unit}${count === 1 ? '' : 's'} ago`;
}

/**
 * Compact relative time. Values less than a minute old (including clock skew
 * that puts them in the future) read "just now"; anything at or beyond 30
 * days falls back to the absolute local timestamp.
 */
export function formatRelativeTime(
  value: string,
  now: Date = new Date(),
): string {
  const date = parseDate(value);

  if (date === null) {
    return formatAbsoluteTime(value);
  }

  const seconds = Math.floor((now.getTime() - date.getTime()) / 1000);

  if (seconds < MINUTE_SECONDS) {
    return 'just now';
  }

  if (seconds < HOUR_SECONDS) {
    return plural(Math.floor(seconds / MINUTE_SECONDS), 'minute');
  }

  if (seconds < DAY_SECONDS) {
    return plural(Math.floor(seconds / HOUR_SECONDS), 'hour');
  }

  const days = Math.floor(seconds / DAY_SECONDS);
  if (days < RELATIVE_DAY_LIMIT) {
    return plural(days, 'day');
  }

  return formatAbsoluteTime(value);
}
