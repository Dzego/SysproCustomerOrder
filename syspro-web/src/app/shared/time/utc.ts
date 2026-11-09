const HAS_OFFSET = /(Z|[+-]\d{2}:?\d{2})$/i;

/**
 * Parses an API timestamp that is always UTC. Values read back from the
 * database arrive without an offset ("2026-11-09T10:15:00.1234567"), and
 * `new Date()` would read those as local time, so "Z" is added when missing.
 */
export function parseUtc(value: string): Date {
  return new Date(HAS_OFFSET.test(value) ? value : `${value}Z`);
}
