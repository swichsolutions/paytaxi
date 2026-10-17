import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { MockDataService } from '../services/mock-data.service';
import { environment } from '../../../environments/environment';

/**
 * Tells the API which language the driver app is showing (Accept-Language: ka | en | ru) on every driver call, the
 * sign-in ones included. The server keeps it with the driver and words the messages it sends itself — the SMS about a
 * payout-account change, also one made by the park from the console — in it. Accept-Language is a CORS-safelisted
 * header, so it costs no preflight.
 */
export const driverLanguageInterceptor: HttpInterceptorFn = (req, next) => {
  const isDriverApi = (req.url.startsWith(environment.apiBase) || req.url.startsWith('/api/')) && req.url.includes('/api/driver/');
  if (!isDriverApi || req.headers.has('Accept-Language')) return next(req);
  return next(req.clone({ setHeaders: { 'Accept-Language': inject(MockDataService).lang() } }));
};
