import { useState } from 'react';
import type {
  Breadcrumb,
  EventDetail,
  EventPayload,
  EventUser,
  ExceptionValue,
  SdkInfo,
  StackFrame,
} from '../api/types';
import { useI18n, type Translate } from '../i18n';
import { LevelBadge } from '../ui/LevelBadge';
import { formatClockTime } from '../ui/time';
import './event.css';

const USER_FIELDS: ReadonlyArray<[keyof EventUser, string]> = [
  ['id', 'event.userFields.id'],
  ['username', 'event.userFields.username'],
  ['email', 'event.userFields.email'],
  ['ip_address', 'event.userFields.ipAddress'],
];

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim() !== '';
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function hasEntries<T extends object>(value: T | null | undefined): value is T {
  return value !== null && value !== undefined && Object.keys(value).length > 0;
}

function ValueCell({ value }: { value: unknown }) {
  if (value !== null && typeof value === 'object') {
    return <pre>{JSON.stringify(value, null, 2)}</pre>;
  }

  return <>{String(value)}</>;
}

function KeyValueTable({
  entries,
}: {
  entries: ReadonlyArray<readonly [string, unknown]>;
}) {
  const { t } = useI18n();

  return (
    <table className="key-value-table">
      <thead>
        <tr>
          <th scope="col">{t('event.key')}</th>
          <th scope="col">{t('event.value')}</th>
        </tr>
      </thead>
      <tbody>
        {entries.map(([key, value]) => (
          <tr key={key}>
            <th scope="row">{key}</th>
            <td>
              <ValueCell value={value} />
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function frameLabel(frame: StackFrame, fallback: string): string {
  const moduleName = isNonEmptyString(frame.module) ? frame.module : null;
  const functionName = isNonEmptyString(frame.function) ? frame.function : null;

  if (moduleName !== null && functionName !== null) {
    return `${moduleName}.${functionName}`;
  }

  if (moduleName !== null) {
    return moduleName;
  }

  if (functionName !== null) {
    return functionName;
  }

  return fallback;
}

function frameLocation(frame: StackFrame): string | null {
  if (!isNonEmptyString(frame.filename)) {
    return null;
  }

  return typeof frame.lineno === 'number'
    ? `${frame.filename}:${frame.lineno}`
    : frame.filename;
}

function frameClassName(frame: StackFrame): string {
  if (frame.in_app === true) {
    return 'frame frame-in-app';
  }

  if (frame.in_app === false) {
    return 'frame frame-system';
  }

  // A frame without an explicit in_app flag is treated as an application
  // frame, but without the in-app emphasis.
  return 'frame';
}

function Frame({ frame }: { frame: StackFrame }) {
  const { t } = useI18n();
  const location = frameLocation(frame);

  return (
    <div className={frameClassName(frame)} data-testid="frame">
      <div className="frame-title">
        {frameLabel(frame, t('event.unknownFrame'))}
      </div>
      {location !== null && <div className="frame-location">{location}</div>}
      {isNonEmptyString(frame.context_line) && (
        <code className="frame-context">{frame.context_line}</code>
      )}
    </div>
  );
}

function exceptionTitle(exception: ExceptionValue): string {
  return isNonEmptyString(exception.value)
    ? `${exception.type}: ${exception.value}`
    : exception.type;
}

function ExceptionBlock({ exception }: { exception: ExceptionValue }) {
  const { t } = useI18n();
  const [showSystemFrames, setShowSystemFrames] = useState(false);

  // v1 stack traces are ordered caller -> callee, so the last frame is the
  // throw site and belongs at the top.
  const frames = [...(exception.stacktrace?.frames ?? [])].reverse();
  const systemFrameCount = frames.filter(
    (frame) => frame.in_app === false,
  ).length;
  const visibleFrames = showSystemFrames
    ? frames
    : frames.filter((frame) => frame.in_app !== false);

  return (
    <section className="exception" data-testid="exception">
      <h4>{exceptionTitle(exception)}</h4>

      {visibleFrames.map((frame, index) => (
        <Frame
          key={`${frame.filename ?? ''}:${frame.lineno ?? ''}:${index}`}
          frame={frame}
        />
      ))}

      {systemFrameCount > 0 && (
        <button
          type="button"
          className="frame-toggle"
          aria-expanded={showSystemFrames}
          onClick={() => setShowSystemFrames((value) => !value)}
        >
          {showSystemFrames
            ? t('event.hideSystemFrames', { count: systemFrameCount })
            : t('event.showSystemFrames', { count: systemFrameCount })}
        </button>
      )}
    </section>
  );
}

function contextEntries(value: unknown): ReadonlyArray<readonly [string, unknown]> {
  if (isRecord(value)) {
    return Object.entries(value);
  }

  return [['value', value]];
}

function sdkText(sdk: SdkInfo, unknownLabel: string): string {
  const name = isNonEmptyString(sdk.name) ? sdk.name : null;
  const version = isNonEmptyString(sdk.version) ? sdk.version : null;

  if (name !== null && version !== null) {
    return `${name}@${version}`;
  }

  return name ?? version ?? unknownLabel;
}

function userRows(
  user: EventUser,
  t: Translate,
): Array<{ label: string; value: string }> {
  const rows: Array<{ label: string; value: string }> = [];

  for (const [field, labelKey] of USER_FIELDS) {
    const value = user[field];

    if (isNonEmptyString(value)) {
      rows.push({ label: t(labelKey), value });
    }
  }

  return rows;
}

function BreadcrumbItem({ breadcrumb }: { breadcrumb: Breadcrumb }) {
  const { locale, t } = useI18n();
  const data = breadcrumb.data;
  const showData = hasEntries(data);

  return (
    <li className="breadcrumb">
      {isNonEmptyString(breadcrumb.timestamp) && (
        <time dateTime={breadcrumb.timestamp}>
          {formatClockTime(breadcrumb.timestamp, locale)}
        </time>
      )}
      {isNonEmptyString(breadcrumb.category) && (
        <span className="breadcrumb-category">{breadcrumb.category}</span>
      )}
      {isNonEmptyString(breadcrumb.message) && (
        <span className="breadcrumb-message">{breadcrumb.message}</span>
      )}
      {isNonEmptyString(breadcrumb.level) && (
        <LevelBadge level={breadcrumb.level} />
      )}
      {showData && (
        <details className="breadcrumb-data">
          <summary>{t('event.data')}</summary>
          <pre>{JSON.stringify(data, null, 2)}</pre>
        </details>
      )}
    </li>
  );
}

export function EventView({ event }: { event: EventDetail }) {
  const { t } = useI18n();
  const payload: EventPayload = event.payload ?? {};
  const exceptions = Array.isArray(payload.exception) ? payload.exception : [];
  const tags = payload.tags;
  const contexts = payload.contexts;
  const breadcrumbs = Array.isArray(payload.breadcrumbs)
    ? payload.breadcrumbs
    : [];
  const extra = payload.extra;
  const sdk = payload.sdk;
  const user = payload.user;
  const rows = user ? userRows(user, t) : [];

  return (
    <div className="event-view">
      {exceptions.length > 0 && (
        <section className="event-section">
          <h3>{t('event.exceptions')}</h3>
          {[...exceptions].reverse().map((exception, index) => (
            <ExceptionBlock
              key={`${exception.type}:${index}`}
              exception={exception}
            />
          ))}
        </section>
      )}

      {isNonEmptyString(payload.message) && (
        <section className="event-section">
          <h3>{t('event.message')}</h3>
          <pre className="event-message">{payload.message}</pre>
        </section>
      )}

      {hasEntries(tags) && (
        <section className="event-section">
          <h3>{t('event.tags')}</h3>
          <KeyValueTable entries={Object.entries(tags)} />
        </section>
      )}

      {rows.length > 0 && (
        <section className="event-section">
          <h3>{t('event.user')}</h3>
          <table className="key-value-table">
            <tbody>
              {rows.map((row) => (
                <tr key={row.label}>
                  <th scope="row">{row.label}</th>
                  <td>{row.value}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}

      {hasEntries(contexts) && (
        <section className="event-section">
          <h3>{t('event.contexts')}</h3>
          {Object.entries(contexts).map(([name, value]) => (
            <div className="context" key={name}>
              <h4>{name}</h4>
              <KeyValueTable entries={contextEntries(value)} />
            </div>
          ))}
        </section>
      )}

      {breadcrumbs.length > 0 && (
        <section className="event-section">
          <h3>{t('event.breadcrumbs')}</h3>
          <ol className="breadcrumbs">
            {breadcrumbs.map((breadcrumb, index) => (
              <BreadcrumbItem
                key={`${breadcrumb.timestamp ?? ''}:${index}`}
                breadcrumb={breadcrumb}
              />
            ))}
          </ol>
        </section>
      )}

      {sdk != null && (
        <section className="event-section">
          <h3>{t('event.sdk')}</h3>
          <p className="event-sdk">{sdkText(sdk, t('event.unknownSdk'))}</p>
        </section>
      )}

      {hasEntries(extra) && (
        <section className="event-section">
          <h3>{t('event.extra')}</h3>
          <KeyValueTable entries={Object.entries(extra)} />
        </section>
      )}

      {/* Raw payload is always available, and its summary doubles as the
          section heading. */}
      <section className="event-section raw-json">
        <details>
          <summary>{t('event.rawJson')}</summary>
          <pre>{JSON.stringify(event.payload, null, 2)}</pre>
        </details>
      </section>
    </div>
  );
}

export default EventView;
