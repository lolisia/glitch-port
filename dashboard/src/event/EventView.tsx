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
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { ScrollArea } from '@/components/ui/scroll-area';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';

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
    return (
      <pre className="m-0 font-mono text-xs whitespace-pre-wrap break-words">
        {JSON.stringify(value, null, 2)}
      </pre>
    );
  }

  return <>{String(value)}</>;
}

function KeyValueTable({
  entries,
  showHeader = true,
}: {
  entries: ReadonlyArray<readonly [string, unknown]>;
  showHeader?: boolean;
}) {
  const { t } = useI18n();

  return (
    <Table className="key-value-table">
      {showHeader && (
        <TableHeader>
          <TableRow>
            <TableHead>{t('event.key')}</TableHead>
            <TableHead>{t('event.value')}</TableHead>
          </TableRow>
        </TableHeader>
      )}
      <TableBody>
        {entries.map(([key, value]) => (
          <TableRow key={key}>
            <TableHead
              scope="row"
              className="font-mono font-normal text-muted-foreground"
            >
              {key}
            </TableHead>
            <TableCell className="whitespace-normal">
              <ValueCell value={value} />
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
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

/** Token-based tones: application frames keep the destructive accent. */
function frameClassName(frame: StackFrame): string {
  const base =
    'frame mt-1 rounded-sm border-l-[3px] px-3 py-2 font-mono text-xs first:mt-0';

  if (frame.in_app === true) {
    return `${base} frame-in-app border-l-destructive bg-destructive/10`;
  }

  if (frame.in_app === false) {
    return `${base} frame-system border-l-border bg-muted text-muted-foreground`;
  }

  // A frame without an explicit in_app flag is treated as an application
  // frame, but without the in-app emphasis.
  return `${base} border-l-transparent`;
}

function Frame({ frame }: { frame: StackFrame }) {
  const { t } = useI18n();
  const location = frameLocation(frame);
  const titleClassName =
    frame.in_app === false
      ? 'frame-title font-normal text-muted-foreground'
      : 'frame-title font-semibold text-foreground';

  return (
    <div className={frameClassName(frame)} data-testid="frame">
      <div className={titleClassName}>
        {frameLabel(frame, t('event.unknownFrame'))}
      </div>
      {location !== null && (
        <div className="frame-location text-muted-foreground">{location}</div>
      )}
      {isNonEmptyString(frame.context_line) && (
        <code className="frame-context mt-1 block rounded-sm bg-muted px-2 py-1 text-foreground whitespace-pre-wrap">
          {frame.context_line}
        </code>
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
    <Card
      className="exception gap-2 border-border/60 p-3 shadow-none"
      data-testid="exception"
    >
      <h4 className="m-0 text-sm font-semibold">{exceptionTitle(exception)}</h4>

      {visibleFrames.length > 0 && (
        <ScrollArea className="max-h-96">
          <div className="flex flex-col">
            {visibleFrames.map((frame, index) => (
              <Frame
                key={`${frame.filename ?? ''}:${frame.lineno ?? ''}:${index}`}
                frame={frame}
              />
            ))}
          </div>
        </ScrollArea>
      )}

      {systemFrameCount > 0 && (
        <Button
          type="button"
          variant="outline"
          size="xs"
          className="frame-toggle self-start"
          aria-expanded={showSystemFrames}
          onClick={() => setShowSystemFrames((value) => !value)}
        >
          {showSystemFrames
            ? t('event.hideSystemFrames', { count: systemFrameCount })
            : t('event.showSystemFrames', { count: systemFrameCount })}
        </Button>
      )}
    </Card>
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
    <li className="breadcrumb flex flex-wrap items-baseline gap-2 border-b border-border/60 py-2 last:border-b-0">
      {isNonEmptyString(breadcrumb.timestamp) && (
        <time
          className="font-mono text-xs text-muted-foreground"
          dateTime={breadcrumb.timestamp}
        >
          {formatClockTime(breadcrumb.timestamp, locale)}
        </time>
      )}
      {isNonEmptyString(breadcrumb.category) && (
        <span className="breadcrumb-category text-xs font-semibold text-muted-foreground">
          {breadcrumb.category}
        </span>
      )}
      {isNonEmptyString(breadcrumb.message) && (
        <span className="breadcrumb-message text-sm">{breadcrumb.message}</span>
      )}
      {isNonEmptyString(breadcrumb.level) && (
        <LevelBadge level={breadcrumb.level} />
      )}
      {showData && (
        <details className="breadcrumb-data basis-full">
          <summary className="cursor-pointer text-sm text-primary">
            {t('event.data')}
          </summary>
          <pre className="mt-2 rounded-md bg-muted p-3 font-mono text-xs whitespace-pre-wrap break-words">{JSON.stringify(data, null, 2)}</pre>
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
    <div className="event-view flex flex-col gap-6">
      {exceptions.length > 0 && (
        <section className="event-section flex flex-col gap-3">
          <h3 className="m-0 text-base font-semibold">{t('event.exceptions')}</h3>
          {[...exceptions].reverse().map((exception, index) => (
            <ExceptionBlock
              key={`${exception.type}:${index}`}
              exception={exception}
            />
          ))}
        </section>
      )}

      {isNonEmptyString(payload.message) && (
        <section className="event-section flex flex-col gap-2">
          <h3 className="m-0 text-base font-semibold">{t('event.message')}</h3>
          <pre className="event-message m-0 rounded-md bg-muted p-3 font-mono text-sm whitespace-pre-wrap break-words">
            {payload.message}
          </pre>
        </section>
      )}

      {hasEntries(tags) && (
        <section className="event-section flex flex-col gap-2">
          <h3 className="m-0 text-base font-semibold">{t('event.tags')}</h3>
          <KeyValueTable entries={Object.entries(tags)} />
        </section>
      )}

      {rows.length > 0 && (
        <section className="event-section flex flex-col gap-2">
          <h3 className="m-0 text-base font-semibold">{t('event.user')}</h3>
          <KeyValueTable
            entries={rows.map((row) => [row.label, row.value] as const)}
            showHeader={false}
          />
        </section>
      )}

      {hasEntries(contexts) && (
        <section className="event-section flex flex-col gap-2">
          <h3 className="m-0 text-base font-semibold">{t('event.contexts')}</h3>
          <ScrollArea className="max-h-96">
            <div className="flex flex-col gap-4">
              {Object.entries(contexts).map(([name, value]) => (
                <div
                  className="context flex flex-col gap-2 rounded-md border border-border/60 p-3"
                  key={name}
                >
                  <h4 className="m-0 text-sm font-semibold">{name}</h4>
                  <KeyValueTable entries={contextEntries(value)} />
                </div>
              ))}
            </div>
          </ScrollArea>
        </section>
      )}

      {breadcrumbs.length > 0 && (
        <section className="event-section flex flex-col gap-2">
          <h3 className="m-0 text-base font-semibold">
            {t('event.breadcrumbs')}
          </h3>
          <ol className="breadcrumbs m-0 list-none p-0">
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
        <section className="event-section flex flex-col gap-2">
          <h3 className="m-0 text-base font-semibold">{t('event.sdk')}</h3>
          <p className="event-sdk m-0 font-mono text-sm">
            {sdkText(sdk, t('event.unknownSdk'))}
          </p>
        </section>
      )}

      {hasEntries(extra) && (
        <section className="event-section flex flex-col gap-2">
          <h3 className="m-0 text-base font-semibold">{t('event.extra')}</h3>
          <KeyValueTable entries={Object.entries(extra)} />
        </section>
      )}

      {/* Raw payload is always available, and its summary doubles as the
          section heading. */}
      <section className="event-section raw-json">
        <details>
          <summary className="cursor-pointer text-sm text-primary">
            {t('event.rawJson')}
          </summary>
          <pre className="mt-2 max-h-96 overflow-auto rounded-md bg-muted p-3 font-mono text-xs whitespace-pre-wrap break-words">{JSON.stringify(event.payload, null, 2)}</pre>
        </details>
      </section>
    </div>
  );
}

export default EventView;
