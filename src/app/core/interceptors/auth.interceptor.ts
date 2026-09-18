import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { AuthService } from '../services/auth.service';
import { AdminAuthService } from '../../admin/services/admin-auth.service';
import { environment } from '../../../environments/environment';

/**
 * Attaches the right Authorization header to outbound backend calls.
 *
 *   /api/admin/auth/*    → no token (login endpoint)
 *   /api/driver/auth/*   → no token (OTP request/verify)
 *   /api/admin/*         → admin JWT
 *   /api/driver/*        → driver JWT
 *   other origins        → no token (third-party hosts)
 *
 * Strict per-scope: an admin tab can't accidentally invoke driver-scoped
 * endpoints with an admin token, and vice versa. Mismatches result in 401/403
 * from the backend, which is what we want.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const isBackend = req.url.startsWith(environment.apiBase) || req.url.startsWith('/api/');
  if (!isBackend) return next(req);

  // Login endpoints never carry a token
  if (req.url.includes('/auth/login') ||
      req.url.includes('/auth/request-otp') ||
      req.url.includes('/auth/verify-otp')) {
    return next(req);
  }

  const token = req.url.includes('/api/admin/')
    ? inject(AdminAuthService).token()
    : inject(AuthService).token();

  if (!token) return next(req);
  return next(req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }));
};
