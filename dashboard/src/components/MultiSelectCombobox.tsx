import { useCommandState } from 'cmdk';
import { Check, ChevronsUpDown } from 'lucide-react';
import { useState } from 'react';
import { Button } from '@/components/ui/button';
import {
  Command,
  CommandEmpty,
  CommandInput,
  CommandItem,
  CommandList,
} from '@/components/ui/command';
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from '@/components/ui/popover';
import { cn } from '@/lib/utils';

export interface MultiSelectComboboxProps {
  /** Every selectable value, in the order the result must be returned in. */
  options: string[];
  /** The currently selected values. */
  value: string[];
  onChange: (next: string[]) => void;
  ariaLabel: string;
  placeholder?: string;
  searchPlaceholder?: string;
  emptyText?: string;
  clearLabel?: string;
  selectedCountLabel: (n: number) => string;
  className?: string;
}

interface CommandOptionsProps {
  options: string[];
  selected: Set<string>;
  onToggle: (option: string) => void;
  emptyText: string;
  clearLabel: string;
  showClear: boolean;
  onClear: () => void;
}

/**
 * The list lives inside the Command context because it subscribes to cmdk's
 * search/value store. That re-render is what lets the item ref callbacks
 * re-apply the multi-select attributes after every cmdk render.
 */
function CommandOptions({
  options,
  selected,
  onToggle,
  emptyText,
  clearLabel,
  showClear,
  onClear,
}: CommandOptionsProps) {
  useCommandState((state) => `${state.search}\u0000${state.value ?? ''}`);

  return (
    <CommandList>
      <CommandEmpty>{emptyText}</CommandEmpty>
      {options.map((option) => {
        const isSelected = selected.has(option);

        return (
          <CommandItem
            key={option}
            value={option}
            onSelect={() => onToggle(option)}
            /*
             * cmdk writes its own single-selection `aria-selected` over the
             * props, so the multi-select state is applied to the DOM in a ref
             * callback, after cmdk's attributes.
             */
            ref={(node: HTMLDivElement | null) => {
              if (!node) {
                return;
              }

              node.dataset.checked = isSelected ? 'true' : 'false';
              node.setAttribute('aria-selected', String(isSelected));
            }}
          >
            <span className="truncate">{option}</span>
            <Check
              className={cn(
                'ml-auto',
                isSelected ? 'opacity-100' : 'opacity-0',
              )}
            />
          </CommandItem>
        );
      })}
      {showClear && (
        <CommandItem
          key="__clear__"
          value={clearLabel}
          forceMount
          onSelect={onClear}
        >
          {clearLabel}
        </CommandItem>
      )}
    </CommandList>
  );
}

/**
 * A multi-select built from a Popover and a cmdk Command. Selecting an option
 * toggles it without closing the popover; the result is always returned in
 * `options` order.
 */
export function MultiSelectCombobox({
  options,
  value,
  onChange,
  ariaLabel,
  placeholder = '',
  searchPlaceholder = 'Search…',
  emptyText = 'No options found.',
  clearLabel = 'Clear',
  selectedCountLabel,
  className,
}: MultiSelectComboboxProps) {
  const [open, setOpen] = useState(false);
  const selected = new Set(value);

  const triggerLabel =
    value.length === 0
      ? placeholder
      : value.length === 1
        ? value[0]
        : selectedCountLabel(value.length);

  function toggle(option: string) {
    const next = new Set(selected);

    if (next.has(option)) {
      next.delete(option);
    } else {
      next.add(option);
    }

    onChange(options.filter((candidate) => next.has(candidate)));
  }

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button
          type="button"
          variant="outline"
          role="combobox"
          aria-expanded={open}
          aria-label={ariaLabel}
          className={cn('w-full justify-between font-normal', className)}
        >
          {triggerLabel}
          <ChevronsUpDown className="opacity-50" />
        </Button>
      </PopoverTrigger>
      <PopoverContent
        align="start"
        className="w-(--radix-popover-trigger-width) p-0"
      >
        <Command>
          <CommandInput placeholder={searchPlaceholder} />
          <CommandOptions
            options={options}
            selected={selected}
            onToggle={toggle}
            emptyText={emptyText}
            clearLabel={clearLabel}
            showClear={value.length > 0}
            onClear={() => onChange([])}
          />
        </Command>
      </PopoverContent>
    </Popover>
  );
}
