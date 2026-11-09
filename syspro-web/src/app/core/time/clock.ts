import { InjectionToken } from '@angular/core';

/** Returns the current time. */
export type Clock = () => Date;

/**
 * The source of "now" for anything date-relative, such as the dashboard's
 * default period. Tests provide a fixed clock instead of faking global timers,
 * which would also stop zone.js and RxJS scheduling.
 */
export const CLOCK = new InjectionToken<Clock>('CLOCK', {
  providedIn: 'root',
  factory: () => () => new Date(),
});
