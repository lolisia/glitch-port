/**
 * Maps the literal English object to the same shape with plain strings.
 *
 * The English dictionaries use `as const` so the literal values describe the
 * key structure every other locale must provide; this type widens the values
 * back to `string` for the translated dictionaries.
 */
export type DeepStringify<T> = {
  [K in keyof T]: T[K] extends string ? string : DeepStringify<T[K]>;
};
