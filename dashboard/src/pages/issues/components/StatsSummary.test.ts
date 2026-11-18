import { describe, expect, it } from 'vitest';
import { niceAxis } from './StatsSummary';

describe('niceAxis', () => {
  it('keeps a 0-1 axis for an empty window', () => {
    expect(niceAxis(0)).toEqual({ max: 1, ticks: [0, 1] });
  });

  it('uses unit steps for small counts', () => {
    expect(niceAxis(1)).toEqual({ max: 1, ticks: [0, 1] });
    expect(niceAxis(4)).toEqual({ max: 4, ticks: [0, 1, 2, 3, 4] });
  });

  it('rounds up to 1/2/5 x 10^n steps with at most four intervals', () => {
    expect(niceAxis(5)).toEqual({ max: 6, ticks: [0, 2, 4, 6] });
    expect(niceAxis(37)).toEqual({ max: 40, ticks: [0, 10, 20, 30, 40] });
    expect(niceAxis(1234)).toEqual({ max: 1500, ticks: [0, 500, 1000, 1500] });
  });
});
