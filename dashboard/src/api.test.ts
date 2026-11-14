import { afterEach, describe, expect, it, vi } from 'vitest';

afterEach(() => {
  vi.unstubAllEnvs();
  vi.resetModules();
});

describe('authenticated gateway', () => {
  it('keeps browser authentication when no client bearer token is configured', async () => {
    vi.stubEnv('VITE_API_BASE', '');
    vi.stubEnv('VITE_ADMIN_TOKEN', '');
    vi.resetModules();
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, json: async () => [] });
    vi.stubGlobal('fetch', fetchMock);
    const { fetchMessages } = await import('./api');
    await expect(fetchMessages()).resolves.toEqual([]);
    expect(fetchMock).toHaveBeenCalledWith('/api/manage/messages?limit=100', { headers: {} });
  });
});
