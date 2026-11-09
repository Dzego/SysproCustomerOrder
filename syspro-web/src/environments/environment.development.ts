import { AppConfig } from '../app/core/config/app-config';

// Development settings. `ng serve` proxies /api to the ASP.NET Core API on
// http://localhost:5204 (see proxy.conf.json), so the backend needs no CORS policy.
export const environment: AppConfig = {
  production: false,
  environmentName: 'Development',
  apiBaseUrl: '/api',
  importPollIntervalMs: 2000,
};
