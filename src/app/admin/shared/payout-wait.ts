/**
 * What a queued payout is waiting for, in the words the console uses. A queued payout waits for as
 * long as its cause lasts and goes out on its own once the cause is gone (licence agreement §5.5,
 * Annex 3 §5) — the console only has to say what the cause is and who can remove it.
 *
 * The same table as the backend's PayoutWaitCodes (PayTaxi.Infrastructure/Services/PayoutWaitCodes.cs),
 * from which the incident monitor takes an incident's fault, so "Bank not answering" here and "External
 * service" on the incident never disagree. Keep the two in step:
 *   - INSUFFICIENT_FUNDS (INSUFFICIENT_PARK_BALANCE) and NO_PARK_ACCOUNT are park-side;
 *   - BANK_EXCEPTION and *_UNRESOLVED / *_UNKNOWN mean the bank did not answer the last attempt (it may
 *     have been paid — such a payout cannot be cancelled until it does);
 *   - our missing adapter and credential / bank-user problems are setup;
 *   - "bank" is only the bank itself failing: its gateway down, or a TBC_ / BOG_ fault of its own. Our generic
 *     BANK_FAILED / BANK_REPORTED_FAILED say an order failed, not that the bank is down: they are "other";
 *   - AUTHORISATION_REVOKED: the park revoked the platform's authorisation (Annex 3 §7) — it is never sent; a person
 *     cancels it. Park-side, like the funds.
 */
export type WaitCause = 'funds' | 'no_account' | 'setup' | 'unresolved' | 'bank' | 'revoked' | 'other';

/** "INSUFFICIENT_FUNDS: Insufficient funds…" → "INSUFFICIENT_FUNDS". */
export function reasonCode(failureReason: string | null | undefined): string | null {
  if (!failureReason) return null;
  const i = failureReason.indexOf(':');
  return (i > 0 ? failureReason.slice(0, i) : failureReason).trim() || null;
}

const SETUP_CODES = new Set([
  'BANK_ADAPTER_MISSING',
  'TBC_NOT_CONFIGURED', 'TBC_CREDENTIALS_MUST_BE_CHANGED', 'TBC_USER_BLOCKED', 'TBC_AUTHENTICATION_FAILED',
]);

/** Generic codes for a bank gateway that does not answer (BANK_TRANSIENT_503 is the mock bank's). */
const GATEWAY_DOWN_CODES = new Set(['BANK_UNAVAILABLE', 'BANK_TRANSIENT_503']);

/** The bank refused our data: never an outage. */
const REJECTED_DATA_CODES = new Set(['TBC_VALIDATION_ERROR', 'TBC_INCORRECT_INPUT_DATA']);

export function waitCause(code: string | null | undefined): WaitCause {
  if (!code) return 'other';
  if (code === 'AUTHORISATION_REVOKED') return 'revoked';
  if (code === 'INSUFFICIENT_FUNDS' || code === 'INSUFFICIENT_PARK_BALANCE') return 'funds';
  if (code === 'NO_PARK_ACCOUNT') return 'no_account';
  if (code === 'BANK_EXCEPTION' || code.endsWith('_UNRESOLVED') || code.endsWith('_UNKNOWN')) return 'unresolved';
  if (SETUP_CODES.has(code)) return 'setup';
  if (GATEWAY_DOWN_CODES.has(code)) return 'bank';
  if (/^(TBC|BOG)_/.test(code) && !REJECTED_DATA_CODES.has(code)) return 'bank';
  return 'other';
}
