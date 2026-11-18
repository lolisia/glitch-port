import { useI18n } from '../i18n';
import { formatAbsoluteTime, formatRelativeTime } from './time';

export function RelativeTime({ value }: { value: string }) {
  const { locale, t } = useI18n();

  return (
    <time dateTime={value} title={formatAbsoluteTime(value, locale)}>
      {formatRelativeTime(value, { locale, justNow: t('ui.justNow') })}
    </time>
  );
}
