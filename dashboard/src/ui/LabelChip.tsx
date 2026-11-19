import { Badge } from '@/components/ui/badge';
import type { Label } from '../api/types';

const FALLBACK_COLOUR = '#888888';
const BLACK_TEXT = '#000000';
const WHITE_TEXT = '#ffffff';

/**
 * WCAG relative luminance above which black text has the better contrast
 * against the background. 0.179 is the usual cut-off used for the black/white
 * contrast decision.
 */
const BLACK_TEXT_LUMINANCE_THRESHOLD = 0.179;

interface RgbColour {
  r: number;
  g: number;
  b: number;
}

const HEX_COLOUR = /^#([0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})$/i;
const RGB_COLOUR =
  /^rgba?\(\s*(\d+(?:\.\d+)?)\s*,\s*(\d+(?:\.\d+)?)\s*,\s*(\d+(?:\.\d+)?)/i;

function clampChannel(value: number): number {
  return Math.min(255, Math.max(0, value));
}

function parseColour(colour: string): RgbColour | null {
  const trimmed = colour.trim();

  const hexMatch = HEX_COLOUR.exec(trimmed);
  if (hexMatch !== null) {
    const digits = hexMatch[1];
    const rgb =
      digits.length <= 4
        ? digits
            .slice(0, 3)
            .split('')
            .map((digit) => `${digit}${digit}`)
            .join('')
        : digits.slice(0, 6);

    return {
      r: Number.parseInt(rgb.slice(0, 2), 16),
      g: Number.parseInt(rgb.slice(2, 4), 16),
      b: Number.parseInt(rgb.slice(4, 6), 16),
    };
  }

  const rgbMatch = RGB_COLOUR.exec(trimmed);
  if (rgbMatch !== null) {
    return {
      r: clampChannel(Number.parseFloat(rgbMatch[1])),
      g: clampChannel(Number.parseFloat(rgbMatch[2])),
      b: clampChannel(Number.parseFloat(rgbMatch[3])),
    };
  }

  return null;
}

function channelToLinear(channel: number): number {
  const value = channel / 255;

  return value <= 0.04045
    ? value / 12.92
    : ((value + 0.055) / 1.055) ** 2.4;
}

/**
 * Relative luminance of a CSS hex or rgb() colour, or `null` when the value
 * cannot be parsed.
 */
export function relativeLuminance(colour: string): number | null {
  const rgb = parseColour(colour);
  if (rgb === null) {
    return null;
  }

  return (
    0.2126 * channelToLinear(rgb.r) +
    0.7152 * channelToLinear(rgb.g) +
    0.0722 * channelToLinear(rgb.b)
  );
}

/**
 * Black or white, whichever keeps the label text readable on the given
 * background. Unparseable colours fall back to white, the previous behaviour.
 */
export function contrastTextColour(background: string): string {
  const luminance = relativeLuminance(background);

  if (luminance === null) {
    return WHITE_TEXT;
  }

  return luminance > BLACK_TEXT_LUMINANCE_THRESHOLD ? BLACK_TEXT : WHITE_TEXT;
}

export function LabelChip({ label }: { label: Label }) {
  const background = label.color || FALLBACK_COLOUR;

  return (
    <Badge
      className="label-chip"
      style={{
        backgroundColor: background,
        color: contrastTextColour(background),
      }}
    >
      {label.name}
    </Badge>
  );
}
