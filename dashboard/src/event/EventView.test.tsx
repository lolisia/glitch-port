import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactElement } from 'react';
import { beforeEach, describe, expect, it } from 'vitest';
import type { EventDetail, EventPayload } from '../api/types';
import { I18nProvider, LOCALE_STORAGE_KEY } from '../i18n';
import EventView, { EventView as NamedEventView } from './EventView';

function makeEvent(payload: EventPayload): EventDetail {
  return {
    id: 'event-1',
    issue_id: 7,
    timestamp: '2026-11-14T10:11:12',
    received_at: '2026-11-14T10:11:13',
    level: 'error',
    platform: 'csharp',
    environment: 'production',
    release: '1.0.0',
    user_id: null,
    endpoint: '/api/1/envelope/',
    payload,
  };
}

function sectionHeadings(): string[] {
  return screen
    .getAllByRole('heading', { level: 3 })
    .map((heading) => heading.textContent ?? '');
}

/** Renders the event body the way the app does, inside the locale provider. */
function renderView(ui: ReactElement) {
  return render(<I18nProvider>{ui}</I18nProvider>);
}

beforeEach(() => {
  window.localStorage.clear();
});

describe('EventView', () => {
  it('exposes the named and default exports', () => {
    expect(NamedEventView).toBe(EventView);
  });

  it('renders exceptions outermost first and joins type and value', () => {
    renderView(
      <EventView
        event={makeEvent({
          exception: [
            // v1 order: cause (inner) first, outermost exception last.
            { type: 'InvalidOperationException', value: 'inner failure' },
            { type: 'GameLoopException', value: 'outer failure' },
            { type: 'TimeoutError', value: '' },
          ],
        })}
      />,
    );

    const exceptions = screen.getAllByTestId('exception');
    expect(exceptions).toHaveLength(3);

    expect(within(exceptions[0]).getByRole('heading', { level: 4 })).toHaveTextContent(
      'TimeoutError',
    );
    expect(within(exceptions[1]).getByRole('heading', { level: 4 })).toHaveTextContent(
      'GameLoopException: outer failure',
    );
    expect(within(exceptions[2]).getByRole('heading', { level: 4 })).toHaveTextContent(
      'InvalidOperationException: inner failure',
    );
  });

  it('renders the stack throw-site first with module.function, filename:lineno and context_line', () => {
    renderView(
      <EventView
        event={makeEvent({
          exception: [
            {
              type: 'Error',
              value: 'boom',
              stacktrace: {
                frames: [
                  // v1 order: caller first, throw site last.
                  {
                    module: 'Game',
                    function: 'Main',
                    filename: 'Assets/Scripts/Program.cs',
                    lineno: 10,
                    in_app: true,
                  },
                  {
                    module: 'Game',
                    function: 'Update',
                    filename: 'Assets/Scripts/Game.cs',
                    lineno: 42,
                    in_app: true,
                    context_line: 'throw new Error("boom");',
                  },
                  {
                    module: 'Engine',
                    function: 'Tick',
                    filename: 'Assets/Engine/Engine.cs',
                    lineno: 99,
                    in_app: false,
                  },
                ],
              },
            },
          ],
        })}
      />,
    );

    const frames = screen.getAllByTestId('frame');
    expect(frames).toHaveLength(2);

    expect(within(frames[0]).getByText('Game.Update')).toBeInTheDocument();
    expect(
      within(frames[0]).getByText('Assets/Scripts/Game.cs:42'),
    ).toBeInTheDocument();
    expect(within(frames[0]).getByText('throw new Error("boom");')).toHaveClass(
      'frame-context',
    );
    expect(frames[0]).toHaveClass('frame', 'frame-in-app');

    expect(within(frames[1]).getByText('Game.Main')).toBeInTheDocument();
    expect(
      within(frames[1]).getByText('Assets/Scripts/Program.cs:10'),
    ).toBeInTheDocument();
    expect(frames[1]).toHaveClass('frame', 'frame-in-app');

    // The throw-site frame is not rendered yet, only its toggle label.
    expect(screen.queryByText('Engine.Tick')).not.toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Show 1 system frames' }),
    ).toHaveAttribute('aria-expanded', 'false');
  });

  it('reveals system frames in place and hides them again', async () => {
    const user = userEvent.setup();

    renderView(
      <EventView
        event={makeEvent({
          exception: [
            {
              type: 'Error',
              stacktrace: {
                frames: [
                  { function: 'Main', in_app: true },
                  { function: 'Update', in_app: true },
                  { function: 'Tick', in_app: false },
                  { function: 'Render', in_app: false },
                ],
              },
            },
          ],
        })}
      />,
    );

    const toggle = screen.getByRole('button', {
      name: 'Show 2 system frames',
    });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(screen.getAllByTestId('frame')).toHaveLength(2);

    await user.click(toggle);

    const expandedToggle = screen.getByRole('button', {
      name: 'Hide 2 system frames',
    });
    expect(expandedToggle).toHaveAttribute('aria-expanded', 'true');

    const frames = screen.getAllByTestId('frame');
    expect(frames.map((frame) => frame.textContent)).toEqual([
      'Render',
      'Tick',
      'Update',
      'Main',
    ]);
    expect(frames[0]).toHaveClass('frame', 'frame-system');
    expect(frames[1]).toHaveClass('frame', 'frame-system');
    expect(frames[2]).toHaveClass('frame', 'frame-in-app');

    await user.click(expandedToggle);

    expect(
      screen.getByRole('button', { name: 'Show 2 system frames' }),
    ).toHaveAttribute('aria-expanded', 'false');
    expect(screen.getAllByTestId('frame')).toHaveLength(2);
    expect(screen.queryByText('Tick')).not.toBeInTheDocument();
  });

  it('keeps system-frame visibility state per exception', async () => {
    const user = userEvent.setup();

    renderView(
      <EventView
        event={makeEvent({
          exception: [
            {
              type: 'Inner',
              stacktrace: {
                frames: [
                  { function: 'InnerApp', in_app: true },
                  { function: 'InnerSystem', in_app: false },
                ],
              },
            },
            {
              type: 'Outer',
              stacktrace: {
                frames: [
                  { function: 'OuterApp', in_app: true },
                  { function: 'OuterSystem', in_app: false },
                ],
              },
            },
          ],
        })}
      />,
    );

    const exceptions = screen.getAllByTestId('exception');
    expect(exceptions).toHaveLength(2);

    const outerToggle = within(exceptions[0]).getByRole('button', {
      name: 'Show 1 system frames',
    });
    expect(
      within(exceptions[1]).getByRole('button', {
        name: 'Show 1 system frames',
      }),
    ).toHaveAttribute('aria-expanded', 'false');

    await user.click(outerToggle);

    expect(within(exceptions[0]).getByText('OuterSystem')).toBeInTheDocument();
    expect(within(exceptions[0]).getByText('OuterApp')).toBeInTheDocument();
    expect(within(exceptions[1]).queryByText('InnerSystem')).toBeNull();
    expect(within(exceptions[1]).getByText('InnerApp')).toBeInTheDocument();
    expect(
      within(exceptions[1]).getByRole('button', {
        name: 'Show 1 system frames',
      }),
    ).toHaveAttribute('aria-expanded', 'false');

    await user.click(
      within(exceptions[0]).getByRole('button', {
        name: 'Hide 1 system frames',
      }),
    );

    expect(within(exceptions[0]).queryByText('OuterSystem')).toBeNull();
  });

  it('treats frames without in_app as unemphasised application frames', async () => {
    const user = userEvent.setup();

    renderView(
      <EventView
        event={makeEvent({
          exception: [
            {
              type: 'Error',
              stacktrace: {
                frames: [
                  { function: 'ImplicitApp' },
                  { function: 'SystemFrame', in_app: false },
                ],
              },
            },
          ],
        })}
      />,
    );

    const appFrame = screen.getByText('ImplicitApp').closest('.frame');
    expect(appFrame).not.toBeNull();
    expect(appFrame).toHaveClass('frame');
    expect(appFrame).not.toHaveClass('frame-in-app');
    expect(appFrame).not.toHaveClass('frame-system');

    await user.click(
      screen.getByRole('button', { name: 'Show 1 system frames' }),
    );

    const systemFrame = screen.getByText('SystemFrame').closest('.frame');
    expect(systemFrame).toHaveClass('frame', 'frame-system');
    expect(appFrame).toHaveClass('frame');
    expect(appFrame).not.toHaveClass('frame-in-app');
  });

  it('falls back when frame parts, filenames or line numbers are missing', () => {
    renderView(
      <EventView
        event={makeEvent({
          exception: [
            {
              type: 'Error',
              stacktrace: {
                frames: [
                  { module: 'OnlyModule', in_app: true },
                  { function: 'OnlyFunction', in_app: true },
                  {
                    filename: 'Assets/Scripts/Unknown.cs',
                    lineno: null,
                    in_app: true,
                  },
                  {},
                ],
              },
            },
          ],
        })}
      />,
    );

    const frames = screen.getAllByTestId('frame');
    expect(frames.map((frame) => frame.querySelector('.frame-title')?.textContent)).toEqual([
      '<unknown>',
      '<unknown>',
      'OnlyFunction',
      'OnlyModule',
    ]);
    expect(
      frames.map(
        (frame) => frame.querySelector('.frame-location')?.textContent ?? null,
      ),
    ).toEqual([null, 'Assets/Scripts/Unknown.cs', null, null]);
  });

  it('renders the message without exceptions', () => {
    renderView(<EventView event={makeEvent({ message: 'Something went wrong.' })} />);

    expect(
      screen.getByRole('heading', { level: 3, name: 'Message' }),
    ).toBeInTheDocument();
    expect(screen.getByText('Something went wrong.')).toHaveClass(
      'event-message',
    );
    expect(
      screen.queryByRole('heading', { level: 3, name: 'Exceptions' }),
    ).not.toBeInTheDocument();
  });

  it('renders the message alongside exceptions', () => {
    renderView(
      <EventView
        event={makeEvent({
          message: 'Request failed.',
          exception: [{ type: 'HttpException', value: '500' }],
        })}
      />,
    );

    expect(
      screen.getByRole('heading', { level: 3, name: 'Exceptions' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { level: 3, name: 'Message' }),
    ).toBeInTheDocument();
    expect(sectionHeadings()).toEqual(['Exceptions', 'Message']);
    expect(screen.getByText('Request failed.')).toHaveClass('event-message');
    expect(screen.getByText('HttpException: 500')).toBeInTheDocument();
  });

  it('renders tags, user, contexts, breadcrumbs, SDK and extra in order', async () => {
    const user = userEvent.setup();
    const breadcrumbData = {
      target: 'settings',
      position: { x: 4, y: 8 },
    };

    const payload: EventPayload = {
      message: 'Request failed.',
      tags: { region: 'eu-west-1', stage: 'staging' },
      user: {
        id: 'user-42',
        username: 'alice',
        email: 'alice@example.com',
      },
      contexts: {
        os: { name: 'Windows', version: '11' },
        runtime: { engine: 'il2cpp', nested: { scripting: true } },
      },
      breadcrumbs: [
        {
          timestamp: '2026-11-14T10:11:12',
          category: 'navigation',
          message: 'opened menu',
          level: 'info',
          data: breadcrumbData,
        },
        {
          timestamp: '2026-11-14T10:11:14',
          category: 'http',
          message: 'request failed',
        },
      ],
      sdk: { name: 'glitchport.unity', version: '1.2.3' },
      extra: { attempts: 7, response: { status: 500 } },
    };

    const { container } = renderView(<EventView event={makeEvent(payload)} />);

    expect(sectionHeadings()).toEqual([
      'Message',
      'Tags',
      'User',
      'Contexts',
      'Breadcrumbs',
      'SDK',
      'Extra',
    ]);

    expect(screen.getByText('region')).toBeInTheDocument();
    expect(screen.getByText('eu-west-1')).toBeInTheDocument();
    expect(screen.getByText('stage')).toBeInTheDocument();
    expect(screen.getByText('staging')).toBeInTheDocument();

    expect(screen.getByText('ID')).toBeInTheDocument();
    expect(screen.getByText('user-42')).toBeInTheDocument();
    expect(screen.getByText('Username')).toBeInTheDocument();
    expect(screen.getByText('alice')).toBeInTheDocument();
    expect(screen.getByText('Email')).toBeInTheDocument();
    expect(screen.getByText('alice@example.com')).toBeInTheDocument();
    expect(screen.queryByText('IP address')).not.toBeInTheDocument();

    expect(
      screen.getByRole('heading', { level: 4, name: 'os' }),
    ).toBeInTheDocument();
    expect(screen.getByText('Windows')).toBeInTheDocument();
    expect(screen.getByText('11')).toBeInTheDocument();
    const runtimeContext = screen
      .getByRole('heading', { level: 4, name: 'runtime' })
      .closest('.context') as HTMLElement;
    expect(runtimeContext).not.toBeNull();
    expect(within(runtimeContext).getByText('engine')).toBeInTheDocument();
    expect(within(runtimeContext).getByText('il2cpp')).toBeInTheDocument();
    expect(
      within(runtimeContext).getByText(/"scripting": true/),
    ).toBeInTheDocument();

    const breadcrumbs = container.querySelectorAll('li.breadcrumb');
    expect(breadcrumbs).toHaveLength(2);
    const firstBreadcrumb = breadcrumbs[0] as HTMLElement;
    expect(firstBreadcrumb).toHaveTextContent('10:11:12');
    expect(firstBreadcrumb).toHaveTextContent('navigation');
    expect(firstBreadcrumb).toHaveTextContent('opened menu');
    expect(within(firstBreadcrumb).getByText('info')).toHaveClass(
      'level-badge',
      'level-info',
    );
    expect(firstBreadcrumb).toHaveTextContent('data');
    expect(
      firstBreadcrumb.querySelector('details pre')?.textContent,
    ).toBe(JSON.stringify(breadcrumbData, null, 2));

    const details = firstBreadcrumb.querySelector('details');
    expect(details).not.toBeNull();
    expect(details).not.toHaveAttribute('open');
    await user.click(within(firstBreadcrumb).getByText('data'));
    expect(details).toHaveAttribute('open');
    await user.click(within(firstBreadcrumb).getByText('data'));
    expect(details).not.toHaveAttribute('open');

    const secondBreadcrumb = breadcrumbs[1] as HTMLElement;
    expect(secondBreadcrumb).toHaveTextContent('10:11:14');
    expect(secondBreadcrumb).toHaveTextContent('http');
    expect(secondBreadcrumb).toHaveTextContent('request failed');
    expect(secondBreadcrumb.querySelector('details')).toBeNull();

    expect(screen.getByText('glitchport.unity@1.2.3')).toBeInTheDocument();

    const extraSection = screen
      .getByRole('heading', { level: 3, name: 'Extra' })
      .closest('.event-section') as HTMLElement;
    expect(within(extraSection).getByText('attempts')).toBeInTheDocument();
    expect(within(extraSection).getByText('7')).toBeInTheDocument();
    expect(within(extraSection).getByText(/"status": 500/)).toBeInTheDocument();
  });

  it.each([
    [{ name: 'only-name' }, 'only-name'],
    [{ version: '9.9.9' }, '9.9.9'],
    [{ name: 'package', version: '1.0.0' }, 'package@1.0.0'],
    [{}, 'unknown'],
  ])('renders the SDK text %o as %s', (sdk, expected) => {
    renderView(<EventView event={makeEvent({ sdk })} />);

    expect(
      screen.getByRole('heading', { level: 3, name: 'SDK' }),
    ).toBeInTheDocument();
    expect(screen.getByText(expected)).toBeInTheDocument();
  });

  it('renders the pretty-printed raw payload last', () => {
    const payload: EventPayload = {
      message: 'boom',
      extra: { retries: 2 },
    };
    const { container } = renderView(<EventView event={makeEvent(payload)} />);

    const summary = screen.getByText('Raw JSON');
    const details = summary.closest('details');
    expect(details).not.toBeNull();
    expect(details).not.toHaveAttribute('open');
    expect(details?.querySelector('pre')?.textContent).toBe(
      JSON.stringify(payload, null, 2),
    );

    const view = container.querySelector('.event-view');
    expect(view?.lastElementChild).toBe(details?.parentElement);
  });

  it('omits missing or empty sections while still rendering raw JSON', () => {
    const { container } = renderView(
      <EventView
        event={makeEvent({
          message: '',
          exception: [],
          tags: {},
          user: { id: '', username: null },
          contexts: {},
          breadcrumbs: [],
          sdk: null,
          extra: {},
        })}
      />,
    );

    expect(screen.queryAllByRole('heading')).toHaveLength(0);
    expect(screen.queryAllByTestId('exception')).toHaveLength(0);
    expect(screen.queryByText('Message')).not.toBeInTheDocument();
    expect(screen.queryByText('Tags')).not.toBeInTheDocument();
    expect(screen.queryByText('User')).not.toBeInTheDocument();
    expect(screen.queryByText('Contexts')).not.toBeInTheDocument();
    expect(screen.queryByText('Breadcrumbs')).not.toBeInTheDocument();
    expect(screen.queryByText('SDK')).not.toBeInTheDocument();
    expect(screen.queryByText('Extra')).not.toBeInTheDocument();

    const rawSection = container.querySelector('.raw-json');
    expect(rawSection).not.toBeNull();
    expect(container.querySelector('.event-view')?.lastElementChild).toBe(
      rawSection,
    );
    expect(screen.getByText('Raw JSON')).toBeInTheDocument();
  });

  it('renders the sections and system frame toggle in Korean', async () => {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, 'ko');
    const user = userEvent.setup();

    renderView(
      <EventView
        event={makeEvent({
          message: 'boom',
          exception: [
            {
              type: 'Error',
              stacktrace: {
                frames: [
                  { function: 'App', in_app: true },
                  { function: 'EngineTick', in_app: false },
                ],
              },
            },
          ],
          tags: { region: 'eu-west-1' },
          user: { id: 'user-42' },
          breadcrumbs: [
            {
              timestamp: '2026-11-14T10:11:12',
              category: 'navigation',
              message: 'opened menu',
              level: 'info',
            },
          ],
          sdk: {},
        })}
      />,
    );

    expect(sectionHeadings()).toEqual([
      '예외',
      '메시지',
      '태그',
      '사용자',
      '브레드크럼',
      'SDK',
    ]);

    // Raw payload values stay untranslated.
    expect(screen.getByText('eu-west-1')).toBeInTheDocument();
    expect(screen.getByText('user-42')).toBeInTheDocument();
    expect(screen.getByText('ID')).toBeInTheDocument();
    expect(screen.getByText('알 수 없음')).toBeInTheDocument();
    expect(screen.getByText('10:11:12')).toBeInTheDocument();

    const showFrames = screen.getByRole('button', {
      name: '시스템 프레임 1개 표시',
    });
    expect(showFrames).toHaveAttribute('aria-expanded', 'false');

    await user.click(showFrames);
    expect(screen.getByText('EngineTick')).toBeInTheDocument();

    expect(
      screen.getByRole('button', { name: '시스템 프레임 1개 숨기기' }),
    ).toHaveAttribute('aria-expanded', 'true');
  });
});
