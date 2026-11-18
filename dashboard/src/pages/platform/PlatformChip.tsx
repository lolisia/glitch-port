import './platform-chip.css';

/** A neutral chip for one observed platform name. */
export function PlatformChip({ platform }: { platform: string }) {
  return <span className="platform-chip">{platform}</span>;
}

/**
 * The observed platforms of a project as chips. An em dash stands in when no
 * event has been recorded yet, so the cell never collapses to blank.
 */
export function PlatformChips({ platforms }: { platforms: string[] }) {
  if (platforms.length === 0) {
    return <span className="platform-chips-empty">—</span>;
  }

  return (
    <span className="platform-chips">
      {platforms.map((platform) => (
        <PlatformChip key={platform} platform={platform} />
      ))}
    </span>
  );
}
