/**
 * Production environment. `apiBase` must be the public origin of the .NET API
 * (e.g. https://api.paytaxi.ge) and that origin must be listed in the backend's
 * `Cors:AllowedOrigins`. Set it before `npm run build` for a production deploy —
 * see docs/DEPLOY.md.
 */
export const environment = {
  production: true,
  apiBase: 'https://api.paytaxi.ge',
};
