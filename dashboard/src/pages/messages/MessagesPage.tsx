import { useCallback, useEffect, useState } from 'react';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { fetchMessages } from '../../api/client';
import type { Message } from '../../api/types';
import { useI18n } from '../../i18n';
import {
  EmptyState,
  ErrorState,
  LoadingState,
  formatAbsoluteTime,
} from '../../ui';

type LoadState =
  | { status: 'loading' }
  | { status: 'error'; error: unknown }
  | { status: 'ready'; messages: Message[] };

export function MessagesPage() {
  const { locale, t } = useI18n();
  const [state, setState] = useState<LoadState>({ status: 'loading' });

  const load = useCallback(async () => {
    setState({ status: 'loading' });

    try {
      const messages = await fetchMessages();
      setState({ status: 'ready', messages });
    } catch (error) {
      setState({ status: 'error', error });
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const loading = state.status === 'loading';

  return (
    <section className="flex flex-col gap-4" data-testid="messages-page">
      <header className="flex flex-wrap items-center justify-between gap-4">
        <h2 className="text-xl font-semibold tracking-tight">
          {t('messages.title')}
        </h2>
        <Button
          type="button"
          variant="outline"
          onClick={() => void load()}
          disabled={loading}
        >
          {t('common.refresh')}
        </Button>
      </header>

      <Card>
        <CardContent className="flex flex-col gap-4">
          {loading && <LoadingState />}

          {state.status === 'error' && (
            <ErrorState error={state.error} prefix={t('messages.loadError')} />
          )}

          {state.status === 'ready' && state.messages.length === 0 && (
            <EmptyState>{t('messages.empty')}</EmptyState>
          )}

          {state.status === 'ready' && state.messages.length > 0 && (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead scope="col">{t('messages.receivedAt')}</TableHead>
                  <TableHead scope="col">{t('messages.endpoint')}</TableHead>
                  <TableHead scope="col">{t('messages.message')}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {state.messages.map((row) => (
                  <TableRow key={row.id}>
                    <TableCell>
                      {formatAbsoluteTime(row.received_at, locale)}
                    </TableCell>
                    <TableCell>{row.endpoint}</TableCell>
                    <TableCell>{row.message}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>
    </section>
  );
}
