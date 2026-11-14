import { formatAbsoluteTime, formatRelativeTime } from './time';

export function RelativeTime({ value }: { value: string }) {
  return (
    <time dateTime={value} title={formatAbsoluteTime(value)}>
      {formatRelativeTime(value)}
    </time>
  );
}
