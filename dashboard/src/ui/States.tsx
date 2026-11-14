import type { ReactNode } from 'react';
import { ApiError } from '../api/client';

export function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export function LoadingState({ label = 'Loading...' }: { label?: string }) {
  return <p className="state state-loading">{label}</p>;
}

export function EmptyState({ children }: { children: ReactNode }) {
  return <p className="state state-empty">{children}</p>;
}

export function ErrorState({
  error,
  prefix,
}: {
  error: unknown;
  prefix: string;
}) {
  const needsToken = error instanceof ApiError && error.status === 401;

  return (
    <div className="state state-error">
      <p role="alert">
        {prefix}: {toErrorMessage(error)}
      </p>
      {needsToken && (
        <p className="state-hint">
          Set VITE_ADMIN_TOKEN to an admin token accepted by the collector.
        </p>
      )}
    </div>
  );
}
