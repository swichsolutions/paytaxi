import { CanActivateFn, Router } from '@angular/router';
import { PLATFORM_ID, inject } from '@angular/core';
import { isPlatformServer } from '@angular/common';
import { AdminAuthService } from '../services/admin-auth.service';

/** Redirects unauthenticated admin visitors to /admin/login. */
export const adminAuthGuard: CanActivateFn = (_route, state) => {
  if (isPlatformServer(inject(PLATFORM_ID))) return true;
  const auth = inject(AdminAuthService);
  const router = inject(Router);
  if (auth.isAuthenticated()) return true;
  return router.parseUrl(`/admin/login?next=${encodeURIComponent(state.url)}`);
};
