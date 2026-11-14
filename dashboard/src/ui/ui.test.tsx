import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ApiError } from '../api/client';
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

describe('RelativeTime', () => {
  it('renders the relative text with the absolute time as a title', () => {
    const value = new Date(Date.now() - 2 * 60 * 60 * 1000).toISOString();

    render(<RelativeTime value={value} />);

    const time = screen.getByText('2 hours ago');
    expect(time.tagName).toBe('TIME');
    expect(time).toHaveAttribute('datetime', value);
    expect(time).toHaveAttribute('title', formatAbsoluteTime(value));
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
});

describe('StatusBadge', () => {
  it('capitalises the status and keeps the rest of the text', () => {
    render(<StatusBadge status="unresolved" />);

    expect(screen.getByText('Unresolved')).toHaveClass('status-badge');
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

  it('converts non-Error values to text', () => {
    expect(toErrorMessage('plain')).toBe('plain');
    expect(toErrorMessage(new Error('wrapped'))).toBe('wrapped');
  });
});
