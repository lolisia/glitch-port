import type { ReactNode } from 'react';
import { ApiError } from '../api/client';
import { useI18n } from '../i18n';

export function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export function LoadingState({ label }: { label?: string }) {
  const { t } = useI18n();

  return <p className="state state-loading">{label ?? t('common.loading')}</p>;
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
  const { t } = useI18n();
  const needsToken = error instanceof ApiError && error.status === 401;

  return (
    <div className="state state-error">
      <p role="alert">
        {prefix}: {toErrorMessage(error)}
      </p>
      {needsToken && (
        <p className="state-hint">
          {t('ui.tokenHint', { name: 'VITE_ADMIN_TOKEN' })}
        </p>
      )}
    </div>
  );
}
