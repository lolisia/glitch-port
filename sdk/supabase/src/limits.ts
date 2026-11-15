/**
 * Hard limits applied when normalizing an event for the GlitchPort collector.
 *
 * The values mirror the collector's validation rules: anything above them is
 * truncated (or dropped) by the SDK before the event is serialized, so a large
 * payload never turns a reportable error into a rejected request.
 */

/** Name reported in `event.sdk.name`. */
export const SDK_NAME = 'glitchport.supabase';

/** Version reported in `event.sdk.version`. */
export const SDK_VERSION = '0.1.0';

/** Maximum number of exceptions kept on an event. */
export const MAX_EXCEPTIONS = 10;

/** Maximum number of stack frames kept per exception. */
export const MAX_FRAMES = 250;

/** Maximum number of breadcrumbs kept on an event. */
export const MAX_BREADCRUMBS = 100;

/** Maximum number of tags kept on an event. */
export const MAX_TAGS = 50;

/** Maximum number of fingerprint components kept on an event. */
export const MAX_FINGERPRINT = 10;

/** Maximum length of `event.message`. */
export const MAX_MESSAGE_LENGTH = 8192;

/** Maximum length of exception values and other free-form values. */
export const MAX_VALUE_LENGTH = 8192;

/** Maximum length of `event.logger`. */
export const MAX_LOGGER_LENGTH = 128;

/** Maximum length of `event.release`. */
export const MAX_RELEASE_LENGTH = 200;

/** Maximum length of `event.environment`. */
export const MAX_ENVIRONMENT_LENGTH = 64;

/** Maximum length of `event.server_name`. */
export const MAX_SERVER_NAME_LENGTH = 200;

/** Maximum length of an exception type name. */
export const MAX_EXCEPTION_TYPE_LENGTH = 200;

/** Maximum length of a stack frame string (function name, filename, module). */
export const MAX_FRAME_STRING_LENGTH = 1024;

/** Maximum length of a tag key. */
export const MAX_TAG_KEY_LENGTH = 32;

/** Maximum length of a tag value. */
export const MAX_TAG_VALUE_LENGTH = 200;

/** Maximum length of a single fingerprint component. */
export const MAX_FINGERPRINT_LENGTH = 200;

/** Default scope capacity when `maxBreadcrumbs` is not configured. */
export const DEFAULT_MAX_BREADCRUMBS = 50;

/** Default transport timeout in milliseconds. */
export const DEFAULT_TIMEOUT_MS = 3000;

/** Retry delay used when the collector does not send a `Retry-After` header. */
export const DEFAULT_RETRY_AFTER_SECONDS = 5;

/** Maximum serialized size of `event.contexts` in bytes. */
export const MAX_CONTEXTS_BYTES = 65536;

/** Maximum serialized size of `event.extra` in bytes. */
export const MAX_EXTRA_BYTES = 65536;

/** Truncate `value` to at most `maxLength` characters. */
export function truncate(value: string, maxLength: number): string {
  return value.length > maxLength ? value.slice(0, maxLength) : value;
}
