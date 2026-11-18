import type { Locale } from '../locale';
import { en, type Messages } from './en';
import { ko } from './ko';

export { en, ko };
export type { Messages };

/**
 * The dictionaries every translator reads. Typing the map as
 * `Record<Locale, Messages>` keeps each locale structurally complete, so a new
 * locale must implement every English key and cannot add an unknown one.
 */
export const messagesByLocale: Record<Locale, Messages> = { en, ko };
