import { useCallback, useEffect, useState } from 'react';
import { fetchMessages } from './api';
import type { Message } from './api';

type LoadState =
  | { status: 'loading' }
  | { status: 'error'; error: string }
  | { status: 'ready'; messages: Message[] };

function formatReceivedAt(value: string): string {
  const date = new Date(value);

  if (Number.isNaN(date.getTime())) {
    return value;
  }

  const pad = (part: number) => String(part).padStart(2, '0');

  return (
    `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ` +
    `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`
  );
}

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export default function App() {
  const [state, setState] = useState<LoadState>({ status: 'loading' });

  const load = useCallback(async () => {
    setState({ status: 'loading' });

    try {
      const messages = await fetchMessages();
      setState({ status: 'ready', messages });
    } catch (error) {
      setState({ status: 'error', error: toErrorMessage(error) });
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const loading = state.status === 'loading';

  return (
    <main className="page">
      <header className="page-header">
        <h1>glitch-port messages</h1>
        <button type="button" onClick={() => void load()} disabled={loading}>
          Refresh
        </button>
      </header>

      {loading && <p>Loading...</p>}

      {state.status === 'error' && (
        <p role="alert">Failed to load messages: {state.error}</p>
      )}

      {state.status === 'ready' && state.messages.length === 0 && (
        <p>No messages yet.</p>
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
                <td>{formatReceivedAt(row.received_at)}</td>
                <td>{row.endpoint}</td>
                <td>{row.message}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </main>
  );
}
