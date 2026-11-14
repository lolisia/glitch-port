export function LevelBadge({ level }: { level: string }) {
  return (
    <span className={`level-badge level-${level.toLowerCase()}`}>{level}</span>
  );
}
