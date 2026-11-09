import { InjectionToken } from '@angular/core';

/**
 * Runtime settings that differ between environments. Services receive them
 * through dependency injection instead of importing the environment file
 * directly, so tests can provide their own values.
 */
export interface AppConfig {
  readonly production: boolean;
  readonly environmentName: string;
  /** Prefix for every backend call. Relative, so the app and API share an origin. */
  readonly apiBaseUrl: string;
  /** How often an import job's status is requested while it is still running. */
  readonly importPollIntervalMs: number;
}

export const APP_CONFIG = new InjectionToken<AppConfig>('APP_CONFIG');
