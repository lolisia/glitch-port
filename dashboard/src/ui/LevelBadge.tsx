import { useI18n } from '../i18n';
import { translatedLabel } from './labels';

export function LevelBadge({ level }: { level: string }) {
  const { t } = useI18n();
  const label = translatedLabel(t, `ui.level.${level.toLowerCase()}`, level);

  return (
    <span className={`level-badge level-${level.toLowerCase()}`}>{label}</span>
  );
}
