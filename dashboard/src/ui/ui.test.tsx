import { render, screen } from '@testing-library/react';
import type { ReactNode } from 'react';
import { describe, expect, it } from 'vitest';
import { ApiError } from '../api/client';
import { I18nProvider, LOCALE_STORAGE_KEY, type Locale } from '../i18n';
import {
  EmptyState,
  ErrorState,
  LabelChip,
  LevelBadge,
  LoadingState,
  RelativeTime,
  StatusBadge,
  formatAbsoluteTime,
  toErrorMessage,
} from './index';

/**
 * Renders inside the provider with a stored locale, which is also how the
 * provider resolves its initial value in the app.
 */
function renderIn(locale: Locale, ui: ReactNode) {
  window.localStorage.setItem(LOCALE_STORAGE_KEY, locale);

  return render(<I18nProvider>{ui}</I18nProvider>);
}

describe('RelativeTime', () => {
  it('renders the relative text with the absolute time as a title', () => {
    const value = new Date(Date.now() - 2 * 60 * 60 * 1000).toISOString();

    render(<RelativeTime value={value} />);

    const time = screen.getByText('2 hours ago');
    expect(time.tagName).toBe('TIME');
    expect(time).toHaveAttribute('datetime', value);
    expect(time).toHaveAttribute('title', formatAbsoluteTime(value));
  });

  it('renders Korean relative text and the Korean title in the ko locale', () => {
    const value = new Date(Date.now() - 2 * 60 * 60 * 1000).toISOString();

    renderIn('ko', <RelativeTime value={value} />);

    const time = screen.getByText('2시간 전');
    expect(time).toHaveAttribute('datetime', value);
    expect(time).toHaveAttribute('title', formatAbsoluteTime(value, 'ko'));
  });

  it('uses the Korean just-now label for fresh timestamps', () => {
    renderIn('ko', <RelativeTime value={new Date().toISOString()} />);

    expect(screen.getByText('방금 전')).toBeInTheDocument();
  });
});

describe('LevelBadge', () => {
  it.each([
    ['fatal', 'level-fatal'],
    ['error', 'level-error'],
    ['warning', 'level-warning'],
    ['info', 'level-info'],
    ['debug', 'level-debug'],
    ['unknown', 'level-unknown'],
    ['trace', 'level-trace'],
  ])('renders %s with the %s class', (level, className) => {
    render(<LevelBadge level={level} />);

    const badge = screen.getByText(level);
    expect(badge).toHaveClass('level-badge', className);
  });

  it('keeps the original casing in the visible text', () => {
    render(<LevelBadge level="Error" />);

    const badge = screen.getByText('Error');
    expect(badge).toHaveClass('level-badge', 'level-error');
  });

  it.each([
    ['fatal', '치명적'],
    ['error', '오류'],
    ['warning', '경고'],
    ['info', '정보'],
    ['debug', '디버그'],
    ['trace', '추적'],
    ['unknown', '알 수 없음'],
  ])('translates %s to %s in Korean', (level, label) => {
    renderIn('ko', <LevelBadge level={level} />);

    expect(screen.getByText(label)).toHaveClass(
      'level-badge',
      `level-${level}`,
    );
  });

  it('translates a known mixed-case level in Korean', () => {
    renderIn('ko', <LevelBadge level="Error" />);

    expect(screen.getByText('오류')).toHaveClass('level-badge', 'level-error');
  });

  it('keeps an unknown level as-is in Korean', () => {
    renderIn('ko', <LevelBadge level="critical" />);

    expect(screen.getByText('critical')).toHaveClass(
      'level-badge',
      'level-critical',
    );
  });
});

describe('StatusBadge', () => {
  it('capitalises the status and keeps the rest of the text', () => {
    render(<StatusBadge status="unresolved" />);

    expect(screen.getByText('Unresolved')).toHaveClass('status-badge');
  });

  it.each([
    ['unresolved', '미해결'],
    ['resolved', '해결됨'],
    ['ignored', '무시됨'],
  ])('translates %s to %s in Korean', (status, label) => {
    renderIn('ko', <StatusBadge status={status} />);

    expect(screen.getByText(label)).toHaveClass('status-badge');
  });

  it('capitalises an unknown status in Korean too', () => {
    renderIn('ko', <StatusBadge status="pending" />);

    expect(screen.getByText('Pending')).toHaveClass('status-badge');
  });
});

describe('LabelChip', () => {
  it('renders the label name with its configured colour', () => {
    render(<LabelChip label={{ id: 1, name: 'crash', color: '#ff0000' }} />);

    const chip = screen.getByText('crash');
    expect(chip).toHaveClass('label-chip');
    expect(chip).toHaveStyle({ backgroundColor: '#ff0000' });
  });

  it('falls back to grey when the colour is empty', () => {
    render(<LabelChip label={{ id: 2, name: 'platform', color: '' }} />);

    expect(screen.getByText('platform')).toHaveStyle({
      backgroundColor: '#888888',
    });
  });
});

describe('States', () => {
  it('renders the loading label', () => {
    render(<LoadingState />);
    expect(screen.getByText('Loading...')).toBeInTheDocument();
  });

  it('renders a custom loading label and empty content', () => {
    render(
      <>
        <LoadingState label="Fetching..." />
        <EmptyState>Nothing here yet.</EmptyState>
      </>,
    );

    expect(screen.getByText('Fetching...')).toBeInTheDocument();
    expect(screen.getByText('Nothing here yet.')).toBeInTheDocument();
  });

  it('reports error messages with the prefix', () => {
    render(<ErrorState error={new Error('boom')} prefix="Failed to load" />);

    expect(screen.getByRole('alert')).toHaveTextContent(
      'Failed to load: boom',
    );
  });

  it('adds the token guidance for a 401 ApiError', () => {
    render(
      <ErrorState
        error={new ApiError(401, 'HTTP 401', undefined)}
        prefix="Failed to load issues"
      />,
    );

    expect(screen.getByRole('alert')).toHaveTextContent(
      'Failed to load issues: HTTP 401',
    );
    expect(
      screen.getByText(
        'Set VITE_ADMIN_TOKEN to an admin token accepted by the collector.',
      ),
    ).toBeInTheDocument();
  });

  it('omits the token guidance for other errors', () => {
    render(
      <ErrorState
        error={new ApiError(500, 'HTTP 500', undefined)}
        prefix="Failed"
      />,
    );

    expect(screen.queryByText(/VITE_ADMIN_TOKEN/)).not.toBeInTheDocument();
  });

  it('translates the loading label and the token guidance in Korean', () => {
    renderIn(
      'ko',
      <>
        <LoadingState />
        <ErrorState
          error={new ApiError(401, 'HTTP 401', undefined)}
          prefix="불러오기 실패"
        />
      </>,
    );

    expect(screen.getByText('불러오는 중...')).toBeInTheDocument();
    // The prefix is supplied by the screen and the API message is never
    // translated; only the surrounding guidance follows the locale.
    expect(screen.getByRole('alert')).toHaveTextContent(
      '불러오기 실패: HTTP 401',
    );
    expect(
      screen.getByText(
        'VITE_ADMIN_TOKEN에 collector가 허용하는 관리자 토큰을 설정하세요.',
      ),
    ).toBeInTheDocument();
  });

  it('converts non-Error values to text', () => {
    expect(toErrorMessage('plain')).toBe('plain');
    expect(toErrorMessage(new Error('wrapped'))).toBe('wrapped');
  });
});
