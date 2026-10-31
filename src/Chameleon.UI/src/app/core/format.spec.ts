import { fillDays, humanBytes } from './format';

describe('humanBytes', () => {
  it('formats with units', () => {
    expect(humanBytes(0)).toBe('0 B');
    expect(humanBytes(3733)).toBe('3.6 KB');
    expect(humanBytes(5 * 1024 ** 3)).toBe('5.0 GB');
  });
});

describe('fillDays', () => {
  const now = new Date(Date.UTC(2026, 2, 2, 12));

  it('fills missing days with zeros, oldest first, across a month boundary', () => {
    const result = fillDays([{ day: '2026-03-01', down: 10, up: 1 }], 2, now);
    expect(result).toEqual([
      { day: '2026-02-28', down: 0, up: 0 },
      { day: '2026-03-01', down: 10, up: 1 },
      { day: '2026-03-02', down: 0, up: 0 },
    ]);
  });
});
