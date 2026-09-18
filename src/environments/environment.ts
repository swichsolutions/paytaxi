/**
 * Development environment (default for `ng serve` / `ng build --configuration development`).
 * Replaced by environment.prod.ts in production builds via angular.json fileReplacements.
 */
export const environment = {
  production: false,
  /** Backend origin, no trailing slash. All API calls are `${apiBase}/api/...`. */
  apiBase: 'http://localhost:5196',
};
