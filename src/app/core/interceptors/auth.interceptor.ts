import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { AuthService } from '../services/auth.service';
import { AdminAuthService } from '../../admin/services/admin-auth.service';

/**
 * Attaches the right Authorization header to outbound backend calls.
 *
 *   /api/admin/auth/*    → no token (login itself)
 *   /api/admin/*         → admin JWT  (manager console)
 *   /api/driver/auth/*   → no token (OTP request/verify)
 *   /api/driver/*        → driver JWT
 *   anything else        → driver JWT if present (legacy paths the driver
 *                          app still uses; remove once /api/driver/* split lands)
 *   non-localhost:5196   → no token (third-party hosts)
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const driverAuth = inject(AuthService);
  const adminAuth  = inject(AdminAuthService);

  if (!req.url.startsWith('http://localhost:5196')) return next(req);

  // Login endpoints never carry a token
  if (req.url.includes('/auth/login') ||
      req.url.includes('/auth/request-otp') ||
      req.url.includes('/auth/verify-otp')) {
    return next(req);
  }

  const useAdmin = req.url.includes('/api/admin/');
  const token = useAdmin ? adminAuth.token() : driverAuth.token();

  // Fallback: if the path is admin but no admin token, try the driver token
  // so the driver app's current calls to /api/admin/* keep working until the
  // driver-scoped split lands.
  const effective = token ?? (useAdmin ? driverAuth.token() : adminAuth.token());

  if (!effective) return next(req);
  return next(req.clone({ setHeaders: { Authorization: `Bearer ${effective}` } }));
};
