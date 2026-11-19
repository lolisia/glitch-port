import { Badge } from '@/components/ui/badge';

/** A neutral, outlined chip for one observed platform name. */
export function PlatformChip({ platform }: { platform: string }) {
  return (
    <Badge variant="outline" className="platform-chip">
      {platform}
    </Badge>
  );
}

/**
 * The observed platforms of a project as chips. An em dash stands in when no
 * event has been recorded yet, so the cell never collapses to blank.
 */
export function PlatformChips({ platforms }: { platforms: string[] }) {
  if (platforms.length === 0) {
    return (
      <span className="platform-chips-empty text-muted-foreground">—</span>
    );
  }

  return (
    <span className="platform-chips flex flex-wrap gap-1">
      {platforms.map((platform) => (
        <PlatformChip key={platform} platform={platform} />
      ))}
    </span>
  );
}
