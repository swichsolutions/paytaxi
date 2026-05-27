import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { AuthService } from '../services/auth.service';

/**
 * Adds Authorization: Bearer <token> to outbound requests targeting our backend.
 * Other origins (third-party APIs, CDN assets) are untouched.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const token = auth.token();
  const isOurBackend = req.url.startsWith('http://localhost:5196');

  if (!token || !isOurBackend) return next(req);

  return next(req.clone({
    setHeaders: { Authorization: `Bearer ${token}` },
  }));
};
