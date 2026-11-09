import { parseUtc } from './utc';

describe('parseUtc', () => {
  it('reads a timestamp without an offset as UTC, not local time', () => {
    expect(parseUtc('2026-11-09T10:15:00.1234567').toISOString()).toBe('2026-11-09T10:15:00.123Z');
  });

  it('keeps an explicit offset', () => {
    expect(parseUtc('2026-11-09T10:15:00Z').toISOString()).toBe('2026-11-09T10:15:00.000Z');
    expect(parseUtc('2026-11-09T12:15:00+02:00').toISOString()).toBe('2026-11-09T10:15:00.000Z');
  });
});
