import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { from, switchMap, throwError, catchError } from 'rxjs';
import { AuthService, SKIP_AUTH } from '../services/auth.service';
import { AdminAuthService } from '../../admin/services/admin-auth.service';
import { environment } from '../../../environments/environment';

/**
 * Attaches the right Authorization header to outbound backend calls.
 *
 *   /api/admin/auth/*    → no token (login endpoint)
 *   /api/driver/auth/*   → no token (OTP request/verify/refresh/logout; marked SKIP_AUTH)
 *   /api/admin/*         → admin JWT
 *   /api/driver/*        → driver JWT, refreshed from the trusted-device session when stale;
 *                          one silent retry on 401, then back to the OTP screen
 *   other origins        → no token (third-party hosts)
 *
 * Strict per-scope: an admin tab can't accidentally invoke driver-scoped
 * endpoints with an admin token, and vice versa.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const isBackend = req.url.startsWith(environment.apiBase) || req.url.startsWith('/api/');
  if (!isBackend || req.context.get(SKIP_AUTH)) return next(req);

  if (req.url.includes('/auth/login') ||
      req.url.includes('/auth/request-otp') ||
      req.url.includes('/auth/verify-otp') ||
      req.url.includes('/auth/refresh') ||
      req.url.includes('/auth/logout')) {
    return next(req);
  }

  // ── Admin scope: plain bearer, no refresh flow (yet) ─────────────
  // A 401 means the token is expired or revoked: drop it and send the admin back to the
  // login page (keeping where they were), instead of leaving every page erroring.
  if (req.url.includes('/api/admin/')) {
    const adminAuth = inject(AdminAuthService);
    const adminRouter = inject(Router);
    const token = adminAuth.token();
    return next(token ? withBearer(req, token) : req).pipe(
      catchError((err: unknown) => {
        if (err instanceof HttpErrorResponse && err.status === 401 && adminAuth.token() !== null) {
          adminAuth.expire();
          const here = adminRouter.url;
          const next = here.startsWith('/admin') && !here.startsWith('/admin/login') ? here : '/admin/overview';
          adminRouter.navigate(['/admin/login'], { queryParams: { reason: 'expired', next } });
        }
        return throwError(() => err);
      }),
    );
  }

  // ── Driver scope: refresh-before-send, retry-once-on-401 ─────────
  const auth = inject(AuthService);
  const router = inject(Router);

  const send = (token: string | null) => next(token ? withBearer(req, token) : req);

  return from(auth.ensureValidToken()).pipe(
    switchMap(() => send(auth.token())),
    catchError((err: unknown) => {
      if (!(err instanceof HttpErrorResponse) || err.status !== 401) return throwError(() => err);
      // Access token rejected (expired mid-flight, key rotated…): refresh once and retry.
      return from(auth.refresh()).pipe(
        switchMap(ok => {
          if (ok) return send(auth.token());
          router.navigate(['/login'], { queryParams: { reason: 'expired' } });
          return throwError(() => err);
        }),
      );
    }),
  );
};

function withBearer(req: HttpRequest<unknown>, token: string) {
  return req.clone({ setHeaders: { Authorization: `Bearer ${token}` } });
}
