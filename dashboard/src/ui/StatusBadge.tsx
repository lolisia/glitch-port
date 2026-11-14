export function StatusBadge({ status }: { status: string }) {
  const capitalised =
    status === '' ? status : status.charAt(0).toUpperCase() + status.slice(1);

  return <span className="status-badge">{capitalised}</span>;
}
