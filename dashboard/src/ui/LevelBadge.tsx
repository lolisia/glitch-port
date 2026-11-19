import { Badge } from '@/components/ui/badge';
import { cn } from '@/lib/utils';
import { useI18n } from '../i18n';
import { translatedLabel } from './labels';

type LevelBadgeVariant = 'destructive' | 'secondary';

/**
 * The badge variant for every issue level. `fatal` and `error` use the
 * destructive variant; the remaining levels share the neutral secondary
 * variant, tinted by `LEVEL_TONES` where the base palette has a matching token.
 */
const LEVEL_VARIANTS: Record<string, LevelBadgeVariant> = {
  fatal: 'destructive',
  error: 'destructive',
  warning: 'secondary',
  info: 'secondary',
  debug: 'secondary',
  trace: 'secondary',
};

/** Semantic token overrides for levels that are not plain destructive/neutral. */
const LEVEL_TONES: Record<string, string> = {
  warning: 'bg-warning text-warning-foreground',
  info: 'bg-info text-info-foreground',
};

export function LevelBadge({ level }: { level: string }) {
  const { t } = useI18n();
  const lower = level.toLowerCase();
  const label = translatedLabel(t, `ui.level.${lower}`, level);

  return (
    <Badge
      variant={LEVEL_VARIANTS[lower] ?? 'secondary'}
      className={cn('level-badge', `level-${lower}`, LEVEL_TONES[lower])}
    >
      {label}
    </Badge>
  );
}
