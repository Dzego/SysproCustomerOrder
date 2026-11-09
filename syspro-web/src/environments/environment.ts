import { AppConfig } from '../app/core/config/app-config';

// Production settings. The production build expects the API to be served from
// the same origin under /api (for example behind a reverse proxy).
export const environment: AppConfig = {
  production: true,
  environmentName: 'Production',
  apiBaseUrl: '/api',
  importPollIntervalMs: 2000,
};
