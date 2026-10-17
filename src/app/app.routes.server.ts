import { RenderMode, ServerRoute } from '@angular/ssr';

/**
 * Only the public pages (logins, privacy policy, terms of use) are prerendered. Everything else sits behind an auth guard
 * whose state lives in the browser (localStorage), so those routes render on the client:
 * a server pass would always see "logged out" and redirect, which is exactly the bug a
 * full-page refresh on /dashboard used to show.
 */
export const serverRoutes: ServerRoute[] = [
  { path: 'login', renderMode: RenderMode.Prerender },
  { path: 'admin/login', renderMode: RenderMode.Prerender },
  // The legal pages must be readable without JavaScript (banks, crawlers, print): static HTML per language.
  { path: 'privacy', renderMode: RenderMode.Prerender },
  { path: 'privacy/:lang', renderMode: RenderMode.Prerender, getPrerenderParams: async () => [{ lang: 'en' }, { lang: 'ru' }] },
  { path: 'terms', renderMode: RenderMode.Prerender },
  { path: 'terms/:lang', renderMode: RenderMode.Prerender, getPrerenderParams: async () => [{ lang: 'en' }, { lang: 'ru' }] },
  { path: '**', renderMode: RenderMode.Client },
];
