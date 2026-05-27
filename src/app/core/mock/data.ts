export type CashoutStatus = 'pending' | 'processing' | 'completed' | 'failed';
export type BankType = 'BOG' | 'TBC';
export type Lang = 'en' | 'ka' | 'ru';

export interface Driver {
  id: string;
  name: string;
  phone: string;
  parkName: string;
  balance: number;
  currency: string;
}

export interface BankCard {
  id: string;
  maskedPan: string;
  bankType: BankType;
  isDefault: boolean;
}

export interface Cashout {
  id: string;
  amount: number;
  fee: number;
  net: number;
  status: CashoutStatus;
  createdAt: Date;
  card: BankCard;
}

export interface Ride {
  id: string;
  amount: number;
  date: Date;
  from: string;
  to: string;
}

export const MOCK_DRIVER: Driver = {
  id: 'drv_001',
  name: 'გიორგი მამულაშვილი',
  phone: '+995 599 123 456',
  parkName: 'Tbilisi Auto Park #3',
  balance: 847.50,
  currency: 'GEL',
};

export const MOCK_CARDS: BankCard[] = [
  { id: 'card_001', maskedPan: '**** 4521', bankType: 'BOG', isDefault: true },
  { id: 'card_002', maskedPan: '**** 8834', bankType: 'TBC', isDefault: false },
];

export const MOCK_CASHOUTS: Cashout[] = [
  { id: 'co_004', amount: 100, fee: 2, net: 98,  status: 'pending',   createdAt: new Date('2026-05-26T08:00:00'), card: MOCK_CARDS[0] },
  { id: 'co_001', amount: 200, fee: 2, net: 198, status: 'completed', createdAt: new Date('2026-05-25T14:30:00'), card: MOCK_CARDS[0] },
  { id: 'co_002', amount: 150, fee: 2, net: 148, status: 'completed', createdAt: new Date('2026-05-24T09:15:00'), card: MOCK_CARDS[1] },
  { id: 'co_003', amount: 300, fee: 3, net: 297, status: 'failed',    createdAt: new Date('2026-05-23T18:45:00'), card: MOCK_CARDS[0] },
];

export const MOCK_RIDES: Ride[] = [
  { id: 'r_001', amount: 18.50, date: new Date('2026-05-26T07:30:00'), from: 'Rustaveli Ave', to: 'Airport' },
  { id: 'r_002', amount: 8.00,  date: new Date('2026-05-26T06:15:00'), from: 'Vake Park',     to: 'Marjanishvili' },
  { id: 'r_003', amount: 22.00, date: new Date('2026-05-25T21:00:00'), from: 'Didube',        to: 'Saburtalo' },
  { id: 'r_004', amount: 14.50, date: new Date('2026-05-25T18:20:00'), from: 'Isani',         to: 'Vake' },
  { id: 'r_005', amount: 11.00, date: new Date('2026-05-25T15:45:00'), from: 'Gldani',        to: 'City Center' },
];

// i18n translations — all imported synchronously (tiny files, ~5KB each)
import en from '../i18n/en.json';
import ka from '../i18n/ka.json';
import ru from '../i18n/ru.json';

export type Translations = typeof en & Record<string, string>;

export const T: Record<Lang, Translations> = {
  en: en as Translations,
  ka: ka as Translations,
  ru: ru as Translations,
};
