import type { Label } from '../api/types';

const FALLBACK_COLOUR = '#888888';

export function LabelChip({ label }: { label: Label }) {
  return (
    <span
      className="label-chip"
      style={{ backgroundColor: label.color || FALLBACK_COLOUR }}
    >
      {label.name}
    </span>
  );
}
