import { CanActivateFn, Router } from '@angular/router';
import { PLATFORM_ID, inject } from '@angular/core';
import { isPlatformServer } from '@angular/common';
import { AuthService } from '../services/auth.service';

/**
 * Functional guard: bounces unauthenticated users to /login.
 * Apply with `canActivate: [authGuard]` on protected routes.
 */
export const authGuard: CanActivateFn = (_route, state) => {
  // Auth state lives in localStorage; the server can't see it, so let the browser decide.
  if (isPlatformServer(inject(PLATFORM_ID))) return true;
  const auth = inject(AuthService);
  const router = inject(Router);
  if (auth.isAuthenticated()) return true;
  return router.parseUrl(`/login?next=${encodeURIComponent(state.url)}`);
};
