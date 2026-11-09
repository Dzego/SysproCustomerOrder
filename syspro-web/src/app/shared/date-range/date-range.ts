/**
 * Calendar-date helpers. Dates are 'YYYY-MM-DD' strings for UTC calendar days,
 * the format <input type="date"> reads and writes. Strings rather than Date
 * objects avoid time-zone shifts: a Date for "1 November" is a different day
 * once a browser east or west of UTC formats it.
 *
 * UTC days match the API, which stores order dates in UTC.
 */

/** An inclusive range of UTC calendar days. */
export interface DateRange {
  readonly from: string;
  readonly to: string;
}

export type DateRangePresetId = 'last7' | 'last30' | 'last90' | 'yearToDate';

export interface DateRangePreset {
  readonly id: DateRangePresetId;
  readonly label: string;
}

export const DATE_RANGE_PRESETS: readonly DateRangePreset[] = [
  { id: 'last7', label: 'Last 7 days' },
  { id: 'last30', label: 'Last 30 days' },
  { id: 'last90', label: 'Last 90 days' },
  { id: 'yearToDate', label: 'Year to date' },
];

const ISO_DATE = /^(\d{4})-(\d{2})-(\d{2})$/;
const MS_PER_DAY = 86_400_000;

/** True for a real calendar date in 'YYYY-MM-DD' form ('2026-02-30' is false). */
export function isIsoDate(value: string): boolean {
  const match = ISO_DATE.exec(value);

  if (!match) {
    return false;
  }

  const [, year, month, day] = match.map(Number);
  const date = new Date(Date.UTC(year, month - 1, day));

  return (
    date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day
  );
}

/** The UTC calendar day of an instant. */
export function toIsoDate(instant: Date): string {
  return instant.toISOString().slice(0, 10);
}

export function addDays(isoDate: string, days: number): string {
  return toIsoDate(new Date(Date.parse(`${isoDate}T00:00:00Z`) + days * MS_PER_DAY));
}

/** The range a preset covers, ending on (and including) today. */
export function presetRange(preset: DateRangePresetId, today: string): DateRange {
  switch (preset) {
    case 'last7':
      return { from: addDays(today, -6), to: today };
    case 'last30':
      return { from: addDays(today, -29), to: today };
    case 'last90':
      return { from: addDays(today, -89), to: today };
    case 'yearToDate':
      return { from: `${today.slice(0, 4)}-01-01`, to: today };
  }
}

/** The preset that produces exactly this range, or null for a custom range. */
export function matchPreset(range: DateRange, today: string): DateRangePresetId | null {
  const preset = DATE_RANGE_PRESETS.find(({ id }) => {
    const candidate = presetRange(id, today);

    return candidate.from === range.from && candidate.to === range.to;
  });

  return preset?.id ?? null;
}
