import { RenderMode, ServerRoute } from '@angular/ssr';

/**
 * Only the public login pages are prerendered. Everything else sits behind an auth guard
 * whose state lives in the browser (localStorage), so those routes render on the client:
 * a server pass would always see "logged out" and redirect, which is exactly the bug a
 * full-page refresh on /dashboard used to show.
 */
export const serverRoutes: ServerRoute[] = [
  { path: 'login', renderMode: RenderMode.Prerender },
  { path: 'admin/login', renderMode: RenderMode.Prerender },
  { path: '**', renderMode: RenderMode.Client },
];
