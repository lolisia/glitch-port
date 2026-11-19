import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { MultiSelectCombobox } from './MultiSelectCombobox';

const OPTIONS = ['apollo', 'borealis', 'cascade', 'delta'];

function Harness({
  initial = [],
  onChange,
}: {
  initial?: string[];
  onChange?: (next: string[]) => void;
}) {
  const [value, setValue] = useState<string[]>(initial);

  return (
    <MultiSelectCombobox
      options={OPTIONS}
      value={value}
      onChange={(next) => {
        onChange?.(next);
        setValue(next);
      }}
      ariaLabel="Projects"
      placeholder="Select projects"
      searchPlaceholder="Search projects"
      emptyText="No projects found."
      clearLabel="Clear selection"
      selectedCountLabel={(count) => `${count} selected`}
    />
  );
}

function getTrigger() {
  return screen.getByRole('combobox', { name: 'Projects' });
}

describe('MultiSelectCombobox trigger label', () => {
  it('shows the placeholder when nothing is selected', () => {
    render(<Harness />);

    expect(getTrigger()).toHaveTextContent('Select projects');
    expect(getTrigger()).toHaveAttribute('aria-expanded', 'false');
  });

  it('shows the value when exactly one option is selected', () => {
    render(<Harness initial={['borealis']} />);

    expect(getTrigger()).toHaveTextContent('borealis');
  });

  it('shows the selected count when several options are selected', () => {
    const selectedCountLabel = vi.fn((count: number) => `${count} selected`);

    render(
      <MultiSelectCombobox
        options={OPTIONS}
        value={['apollo', 'delta']}
        onChange={() => {}}
        ariaLabel="Projects"
        placeholder="Select projects"
        selectedCountLabel={selectedCountLabel}
      />,
    );

    expect(getTrigger()).toHaveTextContent('2 selected');
    expect(selectedCountLabel).toHaveBeenCalledWith(2);
  });
});

describe('MultiSelectCombobox interaction', () => {
  it('opens the popover and lists every option', async () => {
    const user = userEvent.setup();
    render(<Harness />);

    await user.click(getTrigger());

    expect(getTrigger()).toHaveAttribute('aria-expanded', 'true');
    for (const option of OPTIONS) {
      expect(
        screen.getByRole('option', { name: option }),
      ).toBeInTheDocument();
    }
  });

  it('filters the options by the search input', async () => {
    const user = userEvent.setup();
    render(<Harness />);

    await user.click(getTrigger());
    await user.type(screen.getByPlaceholderText('Search projects'), 'bore');

    expect(
      screen.getByRole('option', { name: 'borealis' }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('option', { name: 'apollo' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('option', { name: 'cascade' }),
    ).not.toBeInTheDocument();
  });

  it('shows the empty text when the search matches nothing', async () => {
    const user = userEvent.setup();
    render(<Harness />);

    await user.click(getTrigger());
    await user.type(
      screen.getByPlaceholderText('Search projects'),
      'no-such-option',
    );

    expect(screen.getByText('No projects found.')).toBeInTheDocument();
  });

  it('adds and removes options without closing the popover, in options order', async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    render(<Harness initial={['cascade']} onChange={onChange} />);

    await user.click(getTrigger());
    await user.click(screen.getByRole('option', { name: 'apollo' }));

    expect(onChange).toHaveBeenLastCalledWith(['apollo', 'cascade']);
    expect(getTrigger()).toHaveAttribute('aria-expanded', 'true');
    expect(getTrigger()).toHaveTextContent('2 selected');

    await user.click(screen.getByRole('option', { name: 'delta' }));

    expect(onChange).toHaveBeenLastCalledWith([
      'apollo',
      'cascade',
      'delta',
    ]);

    await user.click(screen.getByRole('option', { name: 'cascade' }));

    expect(onChange).toHaveBeenLastCalledWith(['apollo', 'delta']);
  });

  it('marks selected options with data-checked and aria-selected', async () => {
    const user = userEvent.setup();
    render(<Harness initial={['borealis']} />);

    await user.click(getTrigger());

    const selected = screen.getByRole('option', { name: 'borealis' });
    expect(selected).toHaveAttribute('data-checked', 'true');
    expect(selected).toHaveAttribute('aria-selected', 'true');
    expect(selected.querySelector('svg')).not.toBeNull();

    const unselected = screen.getByRole('option', { name: 'apollo' });
    expect(unselected).toHaveAttribute('data-checked', 'false');
    expect(unselected).toHaveAttribute('aria-selected', 'false');

    await user.click(unselected);

    expect(screen.getByRole('option', { name: 'apollo' })).toHaveAttribute(
      'data-checked',
      'true',
    );
    expect(screen.getByRole('option', { name: 'apollo' })).toHaveAttribute(
      'aria-selected',
      'true',
    );
  });

  it('keeps the selection attributes while filtering', async () => {
    const user = userEvent.setup();
    render(<Harness initial={['apollo', 'borealis']} />);

    await user.click(getTrigger());
    await user.type(screen.getByPlaceholderText('Search projects'), 'apollo');

    const apollo = screen.getByRole('option', { name: 'apollo' });
    expect(apollo).toHaveAttribute('data-checked', 'true');
    expect(apollo).toHaveAttribute('aria-selected', 'true');
    expect(
      screen.queryByRole('option', { name: 'borealis' }),
    ).not.toBeInTheDocument();
  });

  it('clears the selection from the clear item', async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    render(<Harness initial={['apollo', 'delta']} onChange={onChange} />);

    await user.click(getTrigger());
    await user.click(screen.getByRole('option', { name: 'Clear selection' }));

    expect(onChange).toHaveBeenLastCalledWith([]);
    expect(getTrigger()).toHaveTextContent('Select projects');
  });

  it('does not render the clear item when nothing is selected', async () => {
    const user = userEvent.setup();
    render(<Harness />);

    await user.click(getTrigger());

    expect(
      screen.queryByRole('option', { name: 'Clear selection' }),
    ).not.toBeInTheDocument();
  });
});
