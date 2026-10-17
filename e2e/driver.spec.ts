import { test, expect } from '@playwright/test';
import { DRIVERS, driverLogin, tbcIban, freshTail, holdAppScript } from './helpers';

/** Section count of privacy-content.ts — the same in every language. */
const PRIVACY_SECTIONS = 8;

test.describe('driver app', () => {
  test('login: a number typed before the page finished loading survives the app taking over', async ({ page }) => {
    // The login page is prerendered, so on a slow phone the field is usable before the app script runs.
    // Hydration used to clear it (the field's first [value] write), leaving "Send code" disabled.
    const app = await holdAppScript(page);
    await page.goto('/login', { waitUntil: 'commit' });
    await page.locator('input.phone-input').fill('0599 12 34 56');
    await expect.poll(app.requested, 'the app script must still be held while typing').toBe(true);
    app.release();

    // Taken over and normalised like anything typed later (the leading 0 and the spaces go). No code is requested.
    await expect(page.locator('button.btn--primary').first()).toBeEnabled();
    await expect(page.locator('input.phone-input')).toHaveValue('599123456');
  });

  test('login: the language switch is tappable on a 360px phone (the logo used to cover it)', async ({ page }) => {
    await page.goto('/login');
    await page.evaluate(() => localStorage.setItem('paytaxi.lang', 'ka'));
    await page.reload();
    const title = page.locator('.login__body h2').first();
    await expect(title).toContainText('შეიყვანეთ ნომერი');
    // EN is the leftmost button — the one the logo's glow reached under at 360px. A real tap, not a
    // forced click: Playwright refuses when another element is on top.
    await page.locator('.lang-btn', { hasText: 'EN' }).click({ timeout: 5_000 });
    await expect(title).toContainText('Enter your number');
    await page.locator('.lang-btn', { hasText: 'RU' }).click({ timeout: 5_000 });
    await expect(title).toContainText('Введите номер');
  });

  test('privacy policy: public, linked from login in the app language, three languages, fits a 360px phone', async ({ page }) => {
    await page.goto('/login');
    await page.evaluate(() => localStorage.setItem('paytaxi.lang', 'ru'));
    await page.reload();
    await page.locator('.privacy-link').click();
    await page.waitForURL(/\/privacy\/ru$/);
    await expect(page.locator('h1')).toHaveText('Политика конфиденциальности');
    await expect(page.locator('.pp-sec')).toHaveCount(PRIVACY_SECTIONS);
    expect(await page.evaluate(() => document.documentElement.lang)).toBe('ru');

    // Georgian is the governing text at the bare URL; English has its own address too.
    await page.locator('.pp-langs__link', { hasText: 'ქართული' }).click();
    await page.waitForURL(/\/privacy$/);
    await expect(page.locator('h1')).toHaveText('კონფიდენციალურობის პოლიტიკა');
    await expect(page.locator('.pp-govern')).toHaveCount(0);
    await page.goto('/privacy/en#rights');
    await expect(page.locator('h1')).toHaveText('Privacy Policy');
    await expect(page.locator('#rights')).toBeInViewport({ timeout: 5_000 });

    // Unfinished legal details are visibly blank, never invented, and the page says it is a draft.
    await expect(page.locator('.pp-draft')).toBeVisible();
    await expect(page.locator('mark.legal-blank').first()).toBeVisible();
    await expect(page.locator('.pp-sec')).toHaveCount(PRIVACY_SECTIONS);
    // The complaint route is the State Audit Office (the old Personal Data Protection Service is gone).
    await expect(page.locator('#rights')).toContainText('State Audit Office of Georgia (sao.ge)');
    expect(await page.locator('body').innerText()).not.toMatch(/\{[a-zA-Z]+\}|\bundefined\b|\bnull\b|Personal Data Protection Service/);

    const { sw, cw } = await page.evaluate(() => ({ sw: document.documentElement.scrollWidth, cw: document.documentElement.clientWidth }));
    expect(sw, 'privacy page scrolls sideways at 360px').toBeLessThanOrEqual(cw + 1);
  });

  test('login, dashboard balance, cashout step fits one screen, own-name rule', async ({ page }) => {
    await driverLogin(page, DRIVERS.giorgi.phone, 'ka');

    // Dashboard: a real balance, not the "unavailable" state, and the header is not cropped.
    await expect(page.locator('.balance-card__amount')).toContainText('₾');
    await expect(page.locator('.balance-card__amount')).not.toContainText('0.00');
    const park = page.locator('.dash-header__park');
    await expect(park).toBeVisible();
    const clipped = await park.evaluate(el => el.scrollWidth > el.clientWidth + 1 && getComputedStyle(el).textOverflow === 'ellipsis' && getComputedStyle(el).whiteSpace === 'nowrap');
    expect(clipped, 'park name must wrap, not be cut with an ellipsis').toBe(false);

    // Cashout step 1: the Next button is on screen without scrolling at 360×780.
    await page.goto('/cashout');
    await expect(page.locator('.keypad__key:not([disabled])').first()).toBeVisible({ timeout: 15_000 });
    const next = page.locator('.co-body .btn--primary');
    const box = await next.boundingBox();
    const viewport = page.viewportSize()!;
    expect(box, 'Next button must be rendered').not.toBeNull();
    expect(box!.y + box!.height, 'Next button must be above the fold').toBeLessThanOrEqual(viewport.height);

    // Georgian wording: "ანგარიშის ნომერი", never "IBAN", in the driver UI.
    for (const k of ['1', '0']) await page.getByRole('button', { name: k, exact: true }).click();
    await next.click();
    await page.locator('.add-card-row').click();
    await expect(page.locator('.acct-field__label').first()).toBeVisible();
    const labels = await page.locator('.acct-field__label').allTextContents();
    expect(labels[0]).toBe('ანგარიშის ნომერი');
    expect(labels.join(' ')).not.toMatch(/IBAN/);
    await expect(page.locator('.acct-field__hint')).toContainText('პირადობის');

    // Own-name rule: someone else's name is refused with the translated message.
    const inputs = page.locator('.acct-field__input');
    await inputs.nth(0).fill(tbcIban(freshTail()));
    await inputs.nth(1).fill('ნინო ბერიძე');
    await page.locator('.btn--primary.btn--sm').click();
    await expect(page.locator('.amount-error').first()).toContainText('არ ემთხვევა');
  });

  test('profile: holder name is prefilled, refusal is translated, English strings absent', async ({ page }) => {
    await driverLogin(page, DRIVERS.zura.phone, 'en');
    await page.goto('/profile');

    // Your park: the controller's legal name, identification code and phone — the privacy policy
    // sends drivers here (Profile → Your park). Zura drives for Tbilisi #3 (seeded legal details).
    const park = page.locator('.prof-park');
    await expect(park.locator('#prof-park-h')).toHaveText('Your park');
    await expect(park.locator('.prof-park__name')).toHaveText('შპს თბილისი თაქსი სერვისი');
    await expect(park.locator('.prof-park__facts')).toContainText('405123456');
    await expect(park.locator('.prof-park__facts a')).toHaveAttribute('href', 'tel:+995322123456');
    await expect(park.locator('.prof-park__more')).toHaveAttribute('href', '/privacy/en#who-we-are');

    await page.locator('.prof-add').click();
    await expect(page.locator('.prof-field__input').nth(1)).toHaveValue(DRIVERS.zura.name);
    await expect(page.locator('.prof-field__hint')).toContainText('own name');

    await page.locator('.prof-field__input').nth(0).fill(tbcIban(freshTail()));
    await page.locator('.prof-field__input').nth(1).fill('Nino Beridze');
    await page.locator('.prof-form__actions button:not(.btn--ghost)').click();
    await expect(page.locator('.prof-error')).toContainText('does not look like your name');
    // Never Angular's raw HTTP text.
    await expect(page.locator('.prof-error')).not.toContainText('Http failure');
  });

  test('history tab and RU bottom nav fit', async ({ page }) => {
    await driverLogin(page, DRIVERS.valeri.phone, 'ru');
    await page.goto('/history?tab=rides');
    await expect(page.locator('.filter-tab--active')).toHaveText(/поездк/i);   // ?tab=rides selected, in Russian
    // RU nav labels must not overflow their slots.
    const overflow = await page.locator('.nav-item').evaluateAll(items =>
      items.filter(el => { const s = el.querySelector('span'); return s && s.scrollWidth > el.clientWidth + 1; }).length);
    expect(overflow, 'RU bottom-nav label overflows its slot').toBe(0);
  });
});
