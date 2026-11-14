export interface Message {
  id: string;
  received_at: string;
  endpoint: string;
  message: string;
}

export const apiBase = import.meta.env.VITE_API_BASE ?? 'http://localhost:5090';
export const adminToken =
  import.meta.env.VITE_ADMIN_TOKEN ?? 'dev-admin-token';

export async function fetchMessages(): Promise<Message[]> {
  const response = await fetch(apiBase + '/api/manage/messages?limit=100', {
    // An empty token allows a same-origin authenticated gateway to supply it.
    headers: adminToken ? { Authorization: 'Bearer ' + adminToken } : {},
  });

  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`);
  }

  return (await response.json()) as Message[];
}
