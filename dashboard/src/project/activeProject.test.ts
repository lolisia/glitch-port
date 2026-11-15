import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  STORAGE_KEY,
  clearStoredProjectId,
  parseConfiguredProjectId,
  readStoredProjectId,
  resolveActiveProjectId,
  writeStoredProjectId,
} from './activeProject';

beforeEach(() => {
  window.localStorage.clear();
});

afterEach(() => {
  window.localStorage.clear();
});

describe('parseConfiguredProjectId', () => {
  it('accepts positive integer strings', () => {
    expect(parseConfiguredProjectId('1')).toBe(1);
    expect(parseConfiguredProjectId('42')).toBe(42);
    expect(parseConfiguredProjectId(' 7 ')).toBe(7);
  });

  it.each([undefined, '', '   ', '0', '-1', '2.5', 'abc', '1e3', '0x10', '1 2'])(
    'rejects %j and returns null',
    (value) => {
      expect(parseConfiguredProjectId(value)).toBeNull();
    },
  );
});

describe('stored project id', () => {
  it('round-trips a written id', () => {
    writeStoredProjectId(7);

    expect(window.localStorage.getItem(STORAGE_KEY)).toBe('7');
    expect(readStoredProjectId()).toBe(7);
  });

  it('returns null when nothing is stored', () => {
    expect(readStoredProjectId()).toBeNull();
  });

  it.each(['', '   ', '0', '-3', 'abc', '3.5'])(
    'returns null for the invalid stored value %j',
    (value) => {
      window.localStorage.setItem(STORAGE_KEY, value);

      expect(readStoredProjectId()).toBeNull();
    },
  );

  it('clears the stored id', () => {
    writeStoredProjectId(3);
    clearStoredProjectId();

    expect(window.localStorage.getItem(STORAGE_KEY)).toBeNull();
    expect(readStoredProjectId()).toBeNull();
  });

  it('does not throw when storage access fails', () => {
    const getItem = vi
      .spyOn(Storage.prototype, 'getItem')
      .mockImplementation(() => {
        throw new Error('storage disabled');
      });
    const setItem = vi
      .spyOn(Storage.prototype, 'setItem')
      .mockImplementation(() => {
        throw new Error('storage disabled');
      });
    const removeItem = vi
      .spyOn(Storage.prototype, 'removeItem')
      .mockImplementation(() => {
        throw new Error('storage disabled');
      });

    expect(readStoredProjectId()).toBeNull();
    expect(() => writeStoredProjectId(1)).not.toThrow();
    expect(() => clearStoredProjectId()).not.toThrow();

    getItem.mockRestore();
    setItem.mockRestore();
    removeItem.mockRestore();
  });
});

describe('resolveActiveProjectId', () => {
  const ids = [3, 5, 8];

  it('prefers the stored id when it is still in the list', () => {
    expect(resolveActiveProjectId(ids, 5, 8)).toBe(5);
  });

  it('falls back to the configured id when the stored id is gone', () => {
    expect(resolveActiveProjectId(ids, 99, 8)).toBe(8);
  });

  it('falls back to the configured id when nothing is stored', () => {
    expect(resolveActiveProjectId(ids, null, 3)).toBe(3);
  });

  it('falls back to the first project when both ids are missing or invalid', () => {
    expect(resolveActiveProjectId(ids, null, null)).toBe(3);
    expect(resolveActiveProjectId(ids, 99, 42)).toBe(3);
  });

  it('returns null when the list is empty', () => {
    expect(resolveActiveProjectId([], 5, 8)).toBeNull();
  });

  it('does not mutate the resolved list', () => {
    const input = [1, 2];
    const copy = [...input];

    resolveActiveProjectId(input, null, null);

    expect(input).toEqual(copy);
  });
});
