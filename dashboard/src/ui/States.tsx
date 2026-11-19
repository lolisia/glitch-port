import { Loader2 } from 'lucide-react';
import type { ReactNode } from 'react';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { ApiError } from '../api/client';
import { useI18n } from '../i18n';

export function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export function LoadingState({ label }: { label?: string }) {
  const { t } = useI18n();

  return (
    <div
      role="status"
      className="flex items-center gap-2 py-4 text-sm text-muted-foreground"
    >
      <Loader2 aria-hidden="true" className="size-4 animate-spin" />
      <span>{label ?? t('common.loading')}</span>
    </div>
  );
}

export function EmptyState({ children }: { children: ReactNode }) {
  return (
    <p className="py-8 text-center text-sm text-muted-foreground">{children}</p>
  );
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
    <Alert variant="destructive">
      <AlertDescription>
        {prefix}: {toErrorMessage(error)}
      </AlertDescription>
      {needsToken && (
        <p className="col-start-2 text-sm text-muted-foreground">
          {t('ui.tokenHint', { name: 'VITE_ADMIN_TOKEN' })}
        </p>
      )}
    </Alert>
  );
}
