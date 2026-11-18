import { useI18n } from '../i18n';
import { translatedLabel } from './labels';

function capitalise(status: string): string {
  return status === ''
    ? status
    : status.charAt(0).toUpperCase() + status.slice(1);
}

export function StatusBadge({ status }: { status: string }) {
  const { t } = useI18n();
  const label = translatedLabel(
    t,
    `ui.status.${status.toLowerCase()}`,
    capitalise(status),
  );

  return <span className="status-badge">{label}</span>;
}
