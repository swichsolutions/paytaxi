import { CanActivateFn, Router } from '@angular/router';
import { inject } from '@angular/core';
import { AuthService } from '../services/auth.service';

/**
 * Functional guard: bounces unauthenticated users to /login.
 * Apply with `canActivate: [authGuard]` on protected routes.
 */
export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (auth.isAuthenticated()) return true;
  return router.parseUrl(`/login?next=${encodeURIComponent(state.url)}`);
};
