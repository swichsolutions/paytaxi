import { Page, expect } from '@playwright/test';

/** Seeded demo drivers (Development seed). Phone = local 9 digits. */
export const DRIVERS = {
  giorgi: { phone: '599123456', name: 'გიორგი მამულაშვილი', profile: 'yp_tb3_001' },
  nika:   { phone: '597224119', name: 'ნიკა ჯავახიშვილი',   profile: 'yp_tb3_002' },
  beka:   { phone: '599887442', name: 'ბექა გელაშვილი',      profile: 'yp_tb3_004' },
  zura:   { phone: '595412776', name: 'Zura Mikeladze',      profile: 'yp_tb3_006' },
  valeri: { phone: '591661020', name: 'ვალერი თავაძე',       profile: 'yp_tb3_007' },
};

export const ADMINS = {
  swich:    { email: 'ops@swich.dev',          password: 'swich2026!' },
  operator: { email: 'levan@operator.local',   password: 'operator1!' },
};

/** Log a driver in through the real OTP screen (the dev API echoes the code on the page). */
export async function driverLogin(page: Page, phone: string, lang: 'en' | 'ka' | 'ru' = 'en') {
  await page.goto('/login');
  await page.evaluate(l => localStorage.setItem('paytaxi.lang', l), lang);
  await page.reload();
  await page.locator('input.phone-input').fill(phone);
  await page.locator('button.btn--primary').first().click();
  const hint = page.locator('.dev-otp-hint');
  await expect(hint, 'dev OTP code should be echoed — is the API in Development mode?').toBeVisible({ timeout: 15_000 });
  const code = (await hint.textContent())!.match(/\d{6}/)![0];
  // Paste the whole code: the OTP row has a paste handler that fills all six boxes in one go.
  // Typing digit by digit races the auto-advance focus and drops digits under load.
  await page.locator('input.otp-digit').first().evaluate((el, c) => {
    const dt = new DataTransfer();
    dt.setData('text', c);
    el.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true }));
  }, code);
  // Auto-submits when complete; fall back to the button if it did not.
  await page.waitForTimeout(1200);
  if (!page.url().includes('/dashboard')) await page.locator('button.btn--primary').last().click().catch(() => {});
  await page.waitForURL(/\/dashboard/, { timeout: 15_000 });
}

export async function adminLogin(page: Page, who: keyof typeof ADMINS = 'swich') {
  await page.goto('/admin/login');
  await page.fill('input[type="email"]', ADMINS[who].email);
  await page.fill('input[type="password"]', ADMINS[who].password);
  await page.click('button[type="submit"]');
  await page.waitForURL(/\/admin\/overview/, { timeout: 20_000 });
}

/** Valid Georgian TBC IBAN for a 16-digit tail (mod-97 check digits). */
export function tbcIban(tail16: string): string {
  const bban = 'TB' + tail16;
  const numeric = (bban + 'GE00').split('').map(c => (/\d/.test(c) ? c : String(c.charCodeAt(0) - 55))).join('');
  const rem = Number(BigInt(numeric) % 97n);
  return `GE${String(98 - rem).padStart(2, '0')}${bban}`;
}

/** Unique-ish 16-digit tail so repeated runs never collide on an IBAN. */
export function freshTail(): string {
  return String(Date.now()).slice(-13).padStart(16, '7');
}
