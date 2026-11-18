import { act, renderHook } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  ROUTE_CHANGE_EVENT,
  buildHash,
  navigate,
  parseHash,
  useHashRoute,
} from './router';

beforeEach(() => {
  window.history.replaceState(null, '', '/');
});

describe('parseHash', () => {
  it('maps empty hashes to the issues route with an empty query', () => {
    for (const hash of ['', '#', '#/']) {
      const route = parseHash(hash);
      expect(route.name).toBe('issues');
      expect(route.issueId).toBeNull();
      expect(route.projectId).toBeNull();
      expect(Array.from(route.query.entries())).toEqual([]);
    }
  });

  it('maps the known routes and rejects everything else', () => {
    expect(parseHash('#/issues').name).toBe('issues');
    expect(parseHash('#/releases').name).toBe('releases');
    expect(parseHash('#/releases').projectId).toBeNull();
    expect(parseHash('#/releases').issueId).toBeNull();
    expect(parseHash('#/messages').name).toBe('messages');
    expect(parseHash('#/projects').name).toBe('projects');
    expect(parseHash('#/projects').projectId).toBeNull();
    expect(parseHash('#/projects').issueId).toBeNull();

    const detail = parseHash('#/issues/42');
    expect(detail.name).toBe('issue');
    expect(detail.issueId).toBe(42);
    expect(detail.projectId).toBeNull();

    const project = parseHash('#/projects/7');
    expect(project.name).toBe('project');
    expect(project.projectId).toBe(7);
    expect(project.issueId).toBeNull();

    expect(parseHash('#/issues/not-a-number').name).toBe('unknown');
    expect(parseHash('#/projects/0').name).toBe('unknown');
    expect(parseHash('#/projects/-1').name).toBe('unknown');
    expect(parseHash('#/projects/7/keys').name).toBe('unknown');
    expect(parseHash('#/project/7').name).toBe('unknown');
    expect(parseHash('#/messages/extra').name).toBe('unknown');
    expect(parseHash('#/releases/extra').name).toBe('unknown');
    expect(parseHash('#/issues/7/events').name).toBe('unknown');
  });

  it('carries the project id only on the project route', () => {
    expect(parseHash('#/projects/12').projectId).toBe(12);
    expect(parseHash('#/projects/12?tab=keys').projectId).toBe(12);
    expect(parseHash('#/projects/12?tab=keys').query.get('tab')).toBe('keys');
    expect(parseHash('#/projects').projectId).toBeNull();
    expect(parseHash('#/issues').projectId).toBeNull();
    expect(parseHash('#/messages').projectId).toBeNull();
  });

  it('parses the query from the substring after the question mark', () => {
    const route = parseHash('#/issues?status=resolved&q=null');
    expect(route.name).toBe('issues');
    expect(route.query.get('status')).toBe('resolved');
    expect(route.query.get('q')).toBe('null');

    expect(parseHash('#/messages?limit=10').query.get('limit')).toBe('10');
    expect(parseHash('#/releases?environment=production').query.get('environment')).toBe(
      'production',
    );
    expect(parseHash('#/unknown?x=1').query.get('x')).toBe('1');
    expect(parseHash('#/issues?').query.toString()).toBe('');
  });
});

describe('buildHash', () => {
  it('normalises leading slashes', () => {
    expect(buildHash('/issues')).toBe('#/issues');
    expect(buildHash('issues')).toBe('#/issues');
    expect(buildHash('///issues/42')).toBe('#/issues/42');
  });

  it('appends a query only when it is non-empty', () => {
    expect(buildHash('/issues', new URLSearchParams())).toBe('#/issues');
    expect(
      buildHash(
        '/issues',
        new URLSearchParams({ status: 'resolved', q: 'null' }),
      ),
    ).toBe('#/issues?status=resolved&q=null');
  });

  it('builds project hashes from the id and an optional query', () => {
    expect(buildHash('/projects')).toBe('#/projects');
    expect(buildHash('projects/7')).toBe('#/projects/7');
    expect(
      buildHash('/projects/7', new URLSearchParams({ tab: 'keys' })),
    ).toBe('#/projects/7?tab=keys');
  });
});

describe('navigate', () => {
  it('assigns the hash by default so the browser records history', () => {
    navigate('/messages');

    expect(window.location.hash).toBe('#/messages');
    expect(window.location.pathname).toBe('/');
  });

  it('replaces the current entry and notifies listeners', () => {
    const listener = vi.fn();
    window.addEventListener(ROUTE_CHANGE_EVENT, listener);

    try {
      navigate('/issues/42', new URLSearchParams({ tab: 'events' }), {
        replace: true,
      });

      expect(window.location.hash).toBe('#/issues/42?tab=events');
      expect(listener).toHaveBeenCalledTimes(1);
    } finally {
      window.removeEventListener(ROUTE_CHANGE_EVENT, listener);
    }
  });
});

describe('useHashRoute', () => {
  it('updates when the hash changes (back/forward)', () => {
    window.history.replaceState(null, '', '/#/issues');
    const { result } = renderHook(() => useHashRoute());

    expect(result.current.name).toBe('issues');

    act(() => {
      window.history.replaceState(null, '', '/#/messages');
      window.dispatchEvent(new HashChangeEvent('hashchange'));
    });

    expect(result.current.name).toBe('messages');

    act(() => {
      window.history.replaceState(null, '', '/#/issues/7');
      window.dispatchEvent(new HashChangeEvent('hashchange'));
    });

    expect(result.current.name).toBe('issue');
    expect(result.current.issueId).toBe(7);
  });

  it('updates on replace navigation through the route change event', () => {
    window.history.replaceState(null, '', '/#/messages');
    const { result } = renderHook(() => useHashRoute());

    expect(result.current.name).toBe('messages');

    act(() => {
      navigate('/issues', undefined, { replace: true });
    });

    expect(result.current.name).toBe('issues');
  });

  it('keeps the query for filtered routes', () => {
    window.history.replaceState(null, '', '/#/issues?status=resolved');
    const { result } = renderHook(() => useHashRoute());

    expect(result.current.name).toBe('issues');
    expect(result.current.query.get('status')).toBe('resolved');
  });

  it('routes to the project list and detail screens', () => {
    window.history.replaceState(null, '', '/#/projects');
    const { result } = renderHook(() => useHashRoute());

    expect(result.current.name).toBe('projects');

    act(() => {
      window.history.replaceState(null, '', '/#/projects/4');
      window.dispatchEvent(new HashChangeEvent('hashchange'));
    });

    expect(result.current.name).toBe('project');
    expect(result.current.projectId).toBe(4);
  });
});
