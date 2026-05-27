import { CanActivateFn, Router } from '@angular/router';
import { inject } from '@angular/core';
import { AdminAuthService } from '../services/admin-auth.service';

/** Redirects unauthenticated admin visitors to /admin/login. */
export const adminAuthGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AdminAuthService);
  const router = inject(Router);
  if (auth.isAuthenticated()) return true;
  return router.parseUrl(`/admin/login?next=${encodeURIComponent(state.url)}`);
};
