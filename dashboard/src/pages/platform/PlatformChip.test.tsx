import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { applyTheme } from '../../theme/theme';
import { PlatformChip, PlatformChips } from './PlatformChip';

afterEach(() => {
  document.documentElement.removeAttribute('data-theme');
});

describe('PlatformChip', () => {
  it('renders the platform name in a chip', () => {
    render(<PlatformChip platform="unreal" />);

    const chip = screen.getByText('unreal');
    expect(chip).toHaveClass('platform-chip');
    expect(chip).toHaveAttribute('data-variant', 'outline');
    // The outline badge colours come from the theme tokens, so the markup must
    // not pin an inline colour to one theme.
    expect(chip).not.toHaveAttribute('style');
  });
});

describe('PlatformChips', () => {
  it('renders one chip per observed platform', () => {
    const { container } = render(
      <PlatformChips platforms={['unreal', 'unity', 'godot']} />,
    );

    const chips = [...container.querySelectorAll('.platform-chip')];
    expect(chips.map((chip) => chip.textContent)).toEqual([
      'unreal',
      'unity',
      'godot',
    ]);
  });

  it('renders the em dash indicator when no platform was observed', () => {
    render(<PlatformChips platforms={[]} />);

    const empty = screen.getByText('—');
    expect(empty).toHaveClass('platform-chips-empty');
    expect(empty).not.toHaveClass('platform-chip');
  });

  it.each(['light', 'dark'] as const)(
    'renders the chips and the empty indicator in the %s theme',
    (theme) => {
      applyTheme(theme);

      const { rerender } = render(<PlatformChips platforms={['unity']} />);

      expect(document.documentElement.dataset.theme).toBe(theme);
      expect(screen.getByText('unity')).toHaveClass('platform-chip');

      rerender(<PlatformChips platforms={[]} />);

      expect(screen.getByText('—')).toHaveClass('platform-chips-empty');
    },
  );
});
