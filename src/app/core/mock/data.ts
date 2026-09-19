// Shared driver-app types + i18n tables. The mock driver/cards/cashouts/rides that used to live
// here are gone — every page reads real data through DriverSessionService / DriverActivityService.

export type Lang = 'en' | 'ka' | 'ru';

// i18n translations — all imported synchronously (tiny files, ~8KB each)
import en from '../i18n/en.json';
import ka from '../i18n/ka.json';
import ru from '../i18n/ru.json';

export type Translations = typeof en & Record<string, string>;

export const T: Record<Lang, Translations> = {
  en: en as Translations,
  ka: ka as Translations,
  ru: ru as Translations,
};
