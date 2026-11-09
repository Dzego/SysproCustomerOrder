import { addDays, isIsoDate, matchPreset, presetRange, toIsoDate } from './date-range';

describe('date-range helpers', () => {
  describe('isIsoDate', () => {
    it.each(['2026-11-09', '2024-02-29', '2026-12-31'])('accepts %s', (value) => {
      expect(isIsoDate(value)).toBe(true);
    });

    it.each(['', '2026-02-29', '2026-13-01', '2026-11-31', '09/11/2026', '2026-1-9'])(
      'rejects %p',
      (value) => {
        expect(isIsoDate(value)).toBe(false);
      },
    );
  });

  it('takes the UTC calendar day of an instant, not the local one', () => {
    // 23:30 UTC is already the next day in South Africa (UTC+2).
    expect(toIsoDate(new Date('2026-11-09T23:30:00Z'))).toBe('2026-11-09');
  });

  it.each([
    ['2026-11-09', -29, '2026-10-11'],
    ['2026-03-01', -1, '2026-02-28'],
    ['2024-03-01', -1, '2024-02-29'],
    ['2026-12-31', 1, '2027-01-01'],
  ])('addDays(%s, %i) is %s', (date, days, expected) => {
    expect(addDays(date, days)).toBe(expected);
  });

  describe('presetRange', () => {
    const today = '2026-11-09';

    it.each([
      ['last7', '2026-11-03'],
      ['last30', '2026-10-11'],
      ['last90', '2026-08-12'],
      ['yearToDate', '2026-01-01'],
    ] as const)('%s ends today and starts on %s', (preset, from) => {
      expect(presetRange(preset, today)).toEqual({ from, to: today });
    });
  });

  describe('matchPreset', () => {
    it('recognises a range produced by a preset', () => {
      expect(matchPreset({ from: '2026-11-03', to: '2026-11-09' }, '2026-11-09')).toBe('last7');
    });

    it('returns null for a custom range', () => {
      expect(matchPreset({ from: '2026-11-01', to: '2026-11-05' }, '2026-11-09')).toBeNull();
    });
  });
});
