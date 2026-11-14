import { useCallback, useEffect, useState } from 'react';
import { fetchMessages } from '../../api/client';
import type { Message } from '../../api/types';
import {
  EmptyState,
  ErrorState,
  LoadingState,
  formatAbsoluteTime,
} from '../../ui';
import './MessagesPage.css';

type LoadState =
  | { status: 'loading' }
  | { status: 'error'; error: unknown }
  | { status: 'ready'; messages: Message[] };

export function MessagesPage() {
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
    <section className="messages-page" data-testid="messages-page">
      <header className="page-header">
        <h2>Messages</h2>
        <button type="button" onClick={() => void load()} disabled={loading}>
          Refresh
        </button>
      </header>

      {loading && <LoadingState />}

      {state.status === 'error' && (
        <ErrorState error={state.error} prefix="Failed to load messages" />
      )}

      {state.status === 'ready' && state.messages.length === 0 && (
        <EmptyState>No messages yet.</EmptyState>
      )}

      {state.status === 'ready' && state.messages.length > 0 && (
        <table>
          <thead>
            <tr>
              <th scope="col">Received at</th>
              <th scope="col">Endpoint</th>
              <th scope="col">Message</th>
            </tr>
          </thead>
          <tbody>
            {state.messages.map((row) => (
              <tr key={row.id}>
                <td>{formatAbsoluteTime(row.received_at)}</td>
                <td>{row.endpoint}</td>
                <td>{row.message}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
