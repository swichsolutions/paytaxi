import { test, expect, Page } from '@playwright/test';

/**
 * Terms of use: public, linked from the driver login in the app language, three languages,
 * cross-linked with the privacy policy, readable on a 360px phone. Needs no API.
 */

const TERMS = [
  { path: '/terms', lang: 'ka', h1: 'სარგებლობის პირობები', translation: false },
  { path: '/terms/en', lang: 'en', h1: 'Terms of Use', translation: true },
  { path: '/terms/ru', lang: 'ru', h1: 'Условия использования', translation: true },
] as const;

/** Section count of terms-content.ts — the same in every language. */
const TERMS_SECTIONS = 12;

async function setDriverLang(page: Page, lang: 'en' | 'ka' | 'ru') {
  await page.goto('/login');
  await page.evaluate(l => localStorage.setItem('paytaxi.lang', l), lang);
  await page.reload();
}

async function noSidewaysScroll(page: Page, what: string) {
  const { sw, cw } = await page.evaluate(() => ({ sw: document.documentElement.scrollWidth, cw: document.documentElement.clientWidth }));
  expect(sw, `${what} scrolls sideways at ${cw}px`).toBeLessThanOrEqual(cw + 1);
}

test.describe('terms of use', () => {
  test('login: signing in names the terms and the privacy policy, linked in the app language', async ({ page }) => {
    for (const [lang, terms, privacy] of [
      ['ka', '/terms', '/privacy'],
      ['en', '/terms/en', '/privacy/en'],
      ['ru', '/terms/ru', '/privacy/ru'],
    ] as const) {
      await setDriverLang(page, lang);
      const line = page.locator('.legal-accept');
      await expect(line).toBeVisible();
      await expect(line.locator('a.terms-link')).toHaveAttribute('href', terms);
      await expect(line.locator('a.privacy-link')).toHaveAttribute('href', privacy);
      // No stray space before the full stop (the sentence is cut around its two links).
      expect(await line.textContent()).not.toMatch(/\s\.$/);
    }

    // Russian: the link opens the Russian terms.
    await setDriverLang(page, 'ru');
    await page.locator('.legal-accept a.terms-link').click();
    await page.waitForURL(/\/terms\/ru$/);
    await expect(page.locator('h1')).toHaveText('Условия использования');
  });

  test('login: the phone step still fits a 360×640 phone without scrolling', async ({ page }) => {
    await page.setViewportSize({ width: 360, height: 640 });
    for (const lang of ['ka', 'ru', 'en'] as const) {
      await setDriverLang(page, lang);
      await expect(page.locator('.legal-accept')).toBeInViewport({ ratio: 1 });
      const { sh, ch } = await page.evaluate(() => ({ sh: document.documentElement.scrollHeight, ch: document.documentElement.clientHeight }));
      expect(sh, `login (${lang}) scrolls at 360×640`).toBeLessThanOrEqual(ch + 1);
      await noSidewaysScroll(page, `login (${lang})`);
    }
  });

  for (const t of TERMS) {
    test(`${t.path}: heading, html lang, all sections, draft notice, fits 360px`, async ({ page }) => {
      await page.goto(t.path);
      await expect(page.locator('h1')).toHaveText(t.h1);
      expect(await page.evaluate(() => document.documentElement.lang)).toBe(t.lang);
      await expect(page.locator('.pp-sec')).toHaveCount(TERMS_SECTIONS);
      // Privacy-only blocks stay away.
      await expect(page.locator('.pp-summary')).toHaveCount(0);
      await expect(page.locator('.pp-glance')).toHaveCount(0);
      // Georgian is the governing text; the translations say they are translations.
      await expect(page.locator('.pp-govern')).toHaveCount(t.translation ? 1 : 0);
      // Not yet in force: the page says so and the blanks are visible, never invented.
      await expect(page.locator('.pp-draft')).toBeVisible();
      await expect(page.locator('mark.legal-blank').first()).toBeVisible();
      const text = await page.locator('body').innerText();
      expect(text).not.toMatch(/\{[a-zA-Z]+\}|\bundefined\b|\bnull\b/);
      await noSidewaysScroll(page, t.path);
    });
  }

  test('anchors and the language switch keep the document', async ({ page }) => {
    await page.goto('/terms/en#fee');
    await expect(page.locator('#fee')).toBeInViewport({ timeout: 5_000 });
    await page.locator('.pp-langs__link', { hasText: 'ქართული' }).click();
    await page.waitForURL(/\/terms$/);
    await expect(page.locator('h1')).toHaveText('სარგებლობის პირობები');
  });

  test('footer cross-links the terms and the privacy policy in the same language', async ({ page }) => {
    await page.goto('/terms/en');
    await page.locator('.pp-foot__related').click();
    await page.waitForURL(/\/privacy\/en$/);
    await expect(page.locator('h1')).toHaveText('Privacy Policy');

    await page.goto('/privacy');
    await page.locator('.pp-foot__related').click();
    await page.waitForURL(/\/terms$/);
    await expect(page.locator('h1')).toHaveText('სარგებლობის პირობები');
  });
});
