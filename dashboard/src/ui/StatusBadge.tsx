import { Badge } from '@/components/ui/badge';
import { cn } from '@/lib/utils';
import { useI18n } from '../i18n';
import { translatedLabel } from './labels';

/**
 * Statuses with a tone beyond the neutral secondary badge. Unresolved (and any
 * unknown status) stays neutral, a resolved issue turns success green and an
 * ignored one is muted.
 */
const STATUS_TONES: Record<string, string> = {
  resolved: 'bg-success text-success-foreground',
  ignored: 'bg-muted text-muted-foreground',
};

function capitalise(status: string): string {
  return status === ''
    ? status
    : status.charAt(0).toUpperCase() + status.slice(1);
}

export function StatusBadge({ status }: { status: string }) {
  const { t } = useI18n();
  const lower = status.toLowerCase();
  const label = translatedLabel(t, `ui.status.${lower}`, capitalise(status));

  return (
    <Badge
      variant="secondary"
      className={cn('status-badge', STATUS_TONES[lower])}
    >
      {label}
    </Badge>
  );
}
