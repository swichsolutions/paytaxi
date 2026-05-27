// Admin-side extended models and mock data.
// The driver-facing models live in core/mock/data.ts; these extend with park-wide fields.

import { CashoutStatus, BankType } from '../../core/mock/data';

export type DriverStatus = 'active' | 'inactive' | 'suspended';

export interface AdminDriver {
  id: string;
  name: string;
  phone: string;
  yandexProfileId: string;
  status: DriverStatus;
  balance: number;
  totalCashedOutMonth: number;
  cashoutCountMonth: number;
  lastSeenAt: Date;
  joinedAt: Date;
  parkId: string;
  carPlate: string;
}

export interface QueueCashout {
  id: string;
  driverId: string;
  driverName: string;
  amount: number;
  fee: number;
  net: number;
  status: CashoutStatus;
  bankType: BankType;
  maskedPan: string;
  createdAt: Date;
  updatedAt: Date;
  errorMessage?: string;
  bankTransferId?: string;
  yandexTransactionId?: string;
  initiatedBy?: 'driver' | 'manager';
}

export interface AdminPark {
  id: string;
  name: string;
  city: string;
  floatBalance: number;
  floatTarget: number;
  floatMinimum: number;
}

export interface AdminUser {
  id: string;
  name: string;
  role: 'park_manager' | 'admin' | 'reviewer';
  email: string;
}

export interface ActivityEvent {
  id: string;
  at: Date;
  type: 'cashout_submitted' | 'cashout_completed' | 'cashout_failed' | 'driver_joined' | 'float_topped_up' | 'cashout_retried';
  message: string;
  amount?: number;
  driverName?: string;
  severity: 'info' | 'success' | 'warning' | 'danger';
}

// ── Park ──────────────────────────────────────────────────────────
export const MOCK_PARK: AdminPark = {
  id: 'park_tbilisi_3',
  name: 'Tbilisi Auto Park #3',
  city: 'Tbilisi',
  floatBalance: 84_500,
  floatTarget: 125_000,
  floatMinimum: 30_000,
};

export const MOCK_ADMIN_USER: AdminUser = {
  id: 'usr_001',
  name: 'Nika Maisuradze',
  role: 'park_manager',
  email: 'nika@taxipark.ge',
};

// ── Drivers (25 mock entries) ─────────────────────────────────────
// Mix of Georgian, Latin transliterations and high/low activity drivers.
const today = new Date('2026-05-26T18:00:00');

const minutesAgo = (n: number) => new Date(today.getTime() - n * 60_000);
const daysAgo    = (n: number) => new Date(today.getTime() - n * 86_400_000);

export const MOCK_DRIVERS: AdminDriver[] = [
  { id: 'drv_001', name: 'გიორგი მამულაშვილი',    phone: '+995 599 123 456', yandexProfileId: 'yp_a1b2c3', status: 'active',   balance:  847.50, totalCashedOutMonth: 4_320, cashoutCountMonth: 18, lastSeenAt: minutesAgo(8),    joinedAt: daysAgo(412), parkId: MOCK_PARK.id, carPlate: 'AA-123-BB' },
  { id: 'drv_002', name: 'ნიკა ჯავახიშვილი',     phone: '+995 597 224 119', yandexProfileId: 'yp_d4e5f6', status: 'active',   balance: 1_240.00, totalCashedOutMonth: 5_890, cashoutCountMonth: 22, lastSeenAt: minutesAgo(14),   joinedAt: daysAgo(289), parkId: MOCK_PARK.id, carPlate: 'BB-447-CC' },
  { id: 'drv_003', name: 'Levan Kobakhidze',     phone: '+995 555 332 010', yandexProfileId: 'yp_g7h8i9', status: 'active',   balance:   210.00, totalCashedOutMonth: 2_140, cashoutCountMonth:  9, lastSeenAt: minutesAgo(45),   joinedAt: daysAgo(503), parkId: MOCK_PARK.id, carPlate: 'CC-998-DE' },
  { id: 'drv_004', name: 'ბექა გელაშვილი',       phone: '+995 599 887 442', yandexProfileId: 'yp_j1k2l3', status: 'active',   balance:    44.50, totalCashedOutMonth: 1_690, cashoutCountMonth:  8, lastSeenAt: minutesAgo(120),  joinedAt: daysAgo(85),  parkId: MOCK_PARK.id, carPlate: 'AA-771-XR' },
  { id: 'drv_005', name: 'დავით ჩხეიძე',         phone: '+995 593 123 098', yandexProfileId: 'yp_m4n5o6', status: 'active',   balance: 1_980.00, totalCashedOutMonth: 6_240, cashoutCountMonth: 25, lastSeenAt: minutesAgo(3),    joinedAt: daysAgo(720), parkId: MOCK_PARK.id, carPlate: 'TT-222-OK' },
  { id: 'drv_006', name: 'Zura Mikeladze',       phone: '+995 595 412 776', yandexProfileId: 'yp_p7q8r9', status: 'active',   balance:   675.20, totalCashedOutMonth: 3_810, cashoutCountMonth: 16, lastSeenAt: minutesAgo(31),   joinedAt: daysAgo(190), parkId: MOCK_PARK.id, carPlate: 'BB-554-PP' },
  { id: 'drv_007', name: 'ვალერი თავაძე',        phone: '+995 591 661 020', yandexProfileId: 'yp_s1t2u3', status: 'active',   balance:    92.30, totalCashedOutMonth: 1_240, cashoutCountMonth:  6, lastSeenAt: minutesAgo(220),  joinedAt: daysAgo(310), parkId: MOCK_PARK.id, carPlate: 'AB-101-EF' },
  { id: 'drv_008', name: 'ალექსანდრე გვინიაშვილი', phone: '+995 599 410 220', yandexProfileId: 'yp_v4w5x6', status: 'active',   balance:   330.00, totalCashedOutMonth: 2_540, cashoutCountMonth: 11, lastSeenAt: minutesAgo(72),   joinedAt: daysAgo(220), parkId: MOCK_PARK.id, carPlate: 'DD-301-GH' },
  { id: 'drv_009', name: 'ანდრო კიკნაძე',         phone: '+995 555 990 117', yandexProfileId: 'yp_y7z8a9', status: 'active',   balance:   505.00, totalCashedOutMonth: 3_120, cashoutCountMonth: 14, lastSeenAt: minutesAgo(55),   joinedAt: daysAgo(95),  parkId: MOCK_PARK.id, carPlate: 'EE-664-IJ' },
  { id: 'drv_010', name: 'Badri Macharashvili',  phone: '+995 597 333 001', yandexProfileId: 'yp_b1c2d3', status: 'active',   balance: 1_460.75, totalCashedOutMonth: 4_890, cashoutCountMonth: 21, lastSeenAt: minutesAgo(2),    joinedAt: daysAgo(540), parkId: MOCK_PARK.id, carPlate: 'FF-808-KL' },
  { id: 'drv_011', name: 'გელა ცინცაძე',          phone: '+995 593 442 818', yandexProfileId: 'yp_e4f5g6', status: 'inactive', balance:     0.00, totalCashedOutMonth:    0, cashoutCountMonth:  0, lastSeenAt: daysAgo(12),     joinedAt: daysAgo(640), parkId: MOCK_PARK.id, carPlate: 'GG-915-MN' },
  { id: 'drv_012', name: 'Dimitri Lomidze',      phone: '+995 555 600 412', yandexProfileId: 'yp_h7i8j9', status: 'active',   balance:    18.00, totalCashedOutMonth: 1_050, cashoutCountMonth:  5, lastSeenAt: minutesAgo(450),  joinedAt: daysAgo(15),  parkId: MOCK_PARK.id, carPlate: 'HH-022-OP' },
  { id: 'drv_013', name: 'ემზარ ჯაფარიძე',        phone: '+995 591 808 999', yandexProfileId: 'yp_k1l2m3', status: 'active',   balance:   725.50, totalCashedOutMonth: 3_390, cashoutCountMonth: 13, lastSeenAt: minutesAgo(180),  joinedAt: daysAgo(360), parkId: MOCK_PARK.id, carPlate: 'II-441-QR' },
  { id: 'drv_014', name: 'ვახტანგი ჩხარტიშვილი',  phone: '+995 599 727 191', yandexProfileId: 'yp_n4o5p6', status: 'suspended', balance:  130.00, totalCashedOutMonth:    0, cashoutCountMonth:  0, lastSeenAt: daysAgo(5),      joinedAt: daysAgo(880), parkId: MOCK_PARK.id, carPlate: 'JJ-616-ST' },
  { id: 'drv_015', name: 'ზაზა ჯვარაძე',          phone: '+995 555 311 220', yandexProfileId: 'yp_q7r8s9', status: 'active',   balance:   980.00, totalCashedOutMonth: 4_120, cashoutCountMonth: 17, lastSeenAt: minutesAgo(22),   joinedAt: daysAgo(450), parkId: MOCK_PARK.id, carPlate: 'KK-303-UV' },
  { id: 'drv_016', name: 'Kakha Chubinidze',     phone: '+995 597 414 808', yandexProfileId: 'yp_t1u2v3', status: 'active',   balance:   435.30, totalCashedOutMonth: 2_780, cashoutCountMonth: 12, lastSeenAt: minutesAgo(95),   joinedAt: daysAgo(120), parkId: MOCK_PARK.id, carPlate: 'LL-919-WX' },
  { id: 'drv_017', name: 'მამუკა ხატიური',        phone: '+995 593 700 010', yandexProfileId: 'yp_w4x5y6', status: 'active',   balance: 1_120.00, totalCashedOutMonth: 4_640, cashoutCountMonth: 19, lastSeenAt: minutesAgo(7),    joinedAt: daysAgo(580), parkId: MOCK_PARK.id, carPlate: 'MM-525-YZ' },
  { id: 'drv_018', name: 'ნუგზარ შავიშვილი',      phone: '+995 599 188 220', yandexProfileId: 'yp_z7a8b9', status: 'active',   balance:    62.00, totalCashedOutMonth: 1_320, cashoutCountMonth:  7, lastSeenAt: minutesAgo(310),  joinedAt: daysAgo(212), parkId: MOCK_PARK.id, carPlate: 'NN-737-AB' },
  { id: 'drv_019', name: 'Otar Gogoberidze',     phone: '+995 555 220 030', yandexProfileId: 'yp_c1d2e3', status: 'active',   balance:   285.00, totalCashedOutMonth: 2_340, cashoutCountMonth: 10, lastSeenAt: minutesAgo(150),  joinedAt: daysAgo(67),  parkId: MOCK_PARK.id, carPlate: 'OO-148-CD' },
  { id: 'drv_020', name: 'პაატა მარგველაშვილი',   phone: '+995 591 555 717', yandexProfileId: 'yp_f4g5h6', status: 'active',   balance:   745.00, totalCashedOutMonth: 3_450, cashoutCountMonth: 14, lastSeenAt: minutesAgo(40),   joinedAt: daysAgo(390), parkId: MOCK_PARK.id, carPlate: 'PP-868-EF' },
  { id: 'drv_021', name: 'რეზო ჩიქოვანი',         phone: '+995 597 919 008', yandexProfileId: 'yp_i7j8k9', status: 'active',   balance:   1_540.00, totalCashedOutMonth: 5_220, cashoutCountMonth: 21, lastSeenAt: minutesAgo(19),   joinedAt: daysAgo(700), parkId: MOCK_PARK.id, carPlate: 'QQ-454-GH' },
  { id: 'drv_022', name: 'Soso Kakhiani',        phone: '+995 555 700 080', yandexProfileId: 'yp_l1m2n3', status: 'inactive', balance:    25.00, totalCashedOutMonth:  610, cashoutCountMonth:  3, lastSeenAt: daysAgo(8),      joinedAt: daysAgo(250), parkId: MOCK_PARK.id, carPlate: 'RR-161-IJ' },
  { id: 'drv_023', name: 'ტარიელ ჯანდიერი',       phone: '+995 593 414 200', yandexProfileId: 'yp_o4p5q6', status: 'active',   balance:   415.00, totalCashedOutMonth: 2_650, cashoutCountMonth: 11, lastSeenAt: minutesAgo(110),  joinedAt: daysAgo(33),  parkId: MOCK_PARK.id, carPlate: 'SS-272-KL' },
  { id: 'drv_024', name: 'იოსები ბერიძე',         phone: '+995 599 050 100', yandexProfileId: 'yp_r7s8t9', status: 'active',   balance:    88.50, totalCashedOutMonth: 1_410, cashoutCountMonth:  7, lastSeenAt: minutesAgo(260),  joinedAt: daysAgo(155), parkId: MOCK_PARK.id, carPlate: 'TT-383-MN' },
  { id: 'drv_025', name: 'გიორგი ხუციშვილი',      phone: '+995 555 980 020', yandexProfileId: 'yp_u1v2w3', status: 'active',   balance:   650.00, totalCashedOutMonth: 3_080, cashoutCountMonth: 13, lastSeenAt: minutesAgo(60),   joinedAt: daysAgo(425), parkId: MOCK_PARK.id, carPlate: 'UU-494-OP' },
];

// ── Cashout queue ─────────────────────────────────────────────────
// 50 cashouts across various states. Recent ones first.
const driverIdx = (i: number) => MOCK_DRIVERS[i % MOCK_DRIVERS.length];

function mkCashout(
  id: string, driverIdx: number, amount: number, status: CashoutStatus,
  bankType: BankType, maskedPan: string, minsAgo: number,
  errorMessage?: string, initiatedBy: 'driver' | 'manager' = 'driver',
): QueueCashout {
  const d = MOCK_DRIVERS[driverIdx];
  const fee = Math.max(2, parseFloat((amount * 0.01).toFixed(2)));
  return {
    id, driverId: d.id, driverName: d.name, amount, fee, net: amount - fee,
    status, bankType, maskedPan,
    createdAt: minutesAgo(minsAgo),
    updatedAt: minutesAgo(Math.max(0, minsAgo - 1)),
    errorMessage,
    initiatedBy,
    bankTransferId: status === 'completed' ? `bog_tx_${id}` : undefined,
    yandexTransactionId: status === 'completed' ? `yx_${id}` : undefined,
  };
}

export const MOCK_QUEUE: QueueCashout[] = [
  // Active queue (today)
  mkCashout('co_2001',  0, 100, 'pending',    'BOG', '**** 4521',   2),
  mkCashout('co_2002',  4, 250, 'processing', 'BOG', '**** 7102',   4),
  mkCashout('co_2003',  9, 180, 'processing', 'TBC', '**** 8834',   6),
  mkCashout('co_2004', 12,  60, 'failed',     'TBC', '**** 6611',  12, 'Card declined: insufficient destination account info'),
  mkCashout('co_2005',  1, 320, 'pending',    'BOG', '**** 9020',  15),
  mkCashout('co_2006',  5, 140, 'pending',    'TBC', '**** 1284',  19),
  mkCashout('co_2007',  7,  80, 'failed',     'BOG', '**** 4040',  25, 'Bank API timeout — retry queued'),
  mkCashout('co_2008', 15, 410, 'completed',  'BOG', '**** 2255',  35),
  mkCashout('co_2009',  2, 200, 'completed',  'BOG', '**** 4521',  48),
  mkCashout('co_2010', 17, 175, 'completed',  'TBC', '**** 7777',  62),
  mkCashout('co_2011', 19,  95, 'completed',  'BOG', '**** 1212',  78),
  mkCashout('co_2012',  8, 230, 'completed',  'TBC', '**** 5454',  95),
  mkCashout('co_2013', 23, 120, 'completed',  'BOG', '**** 6363',  120),
  mkCashout('co_2014', 16, 290, 'completed',  'TBC', '**** 7117',  150),
  mkCashout('co_2015',  3, 160, 'completed',  'BOG', '**** 9999',  180),
  mkCashout('co_2016', 24, 110, 'completed',  'TBC', '**** 0808',  220),
  mkCashout('co_2017', 20, 340, 'completed',  'BOG', '**** 3399',  260),
  mkCashout('co_2018', 10, 480, 'completed',  'TBC', '**** 2240',  310),
  mkCashout('co_2019',  6, 145, 'completed',  'BOG', '**** 7301',  360),

  // Yesterday
  mkCashout('co_2020',  0, 220, 'completed', 'BOG', '**** 4521',  60 * 18 + 10),
  mkCashout('co_2021',  4, 195, 'completed', 'BOG', '**** 7102',  60 * 19 + 25),
  mkCashout('co_2022',  9, 380, 'completed', 'TBC', '**** 8834',  60 * 20 + 5),
  mkCashout('co_2023', 12,  70, 'completed', 'TBC', '**** 6611',  60 * 21 + 14),
  mkCashout('co_2024', 25 - 8, 410, 'completed', 'BOG', '**** 9020',  60 * 22 + 40),
  mkCashout('co_2025',  5, 130, 'completed', 'TBC', '**** 1284',  60 * 24 + 12),
  mkCashout('co_2026', 14, 200, 'failed', 'BOG', '**** 4040',  60 * 26, 'Driver suspended — cashout blocked', 'manager'),
  mkCashout('co_2027', 17, 260, 'completed', 'BOG', '**** 2255',  60 * 27 + 30),
  mkCashout('co_2028',  2, 110, 'completed', 'BOG', '**** 4521',  60 * 28),
  mkCashout('co_2029', 19, 175, 'completed', 'BOG', '**** 1212',  60 * 30),
  mkCashout('co_2030', 23, 220, 'completed', 'BOG', '**** 6363',  60 * 31),

  // Earlier this week — abbreviated batch
  mkCashout('co_2031',  8, 195, 'completed', 'TBC', '**** 5454',  60 * 48 + 30),
  mkCashout('co_2032', 15, 310, 'completed', 'BOG', '**** 2255',  60 * 49 + 14),
  mkCashout('co_2033',  3, 165, 'completed', 'BOG', '**** 9999',  60 * 50),
  mkCashout('co_2034', 21, 240, 'completed', 'BOG', '**** 1828',  60 * 51),
  mkCashout('co_2035', 16, 410, 'completed', 'TBC', '**** 7117',  60 * 52 + 22),
  mkCashout('co_2036', 24,  90, 'completed', 'TBC', '**** 0808',  60 * 53),
  mkCashout('co_2037', 20, 145, 'completed', 'BOG', '**** 3399',  60 * 55),
  mkCashout('co_2038',  6, 220, 'completed', 'BOG', '**** 7301',  60 * 56),
  mkCashout('co_2039',  9, 300, 'completed', 'TBC', '**** 8834',  60 * 58),
  mkCashout('co_2040', 13, 175, 'completed', 'BOG', '**** 4040',  60 * 60),
  mkCashout('co_2041', 17, 250, 'completed', 'BOG', '**** 2255',  60 * 62 + 30),
  mkCashout('co_2042',  0, 190, 'completed', 'BOG', '**** 4521',  60 * 65),
  mkCashout('co_2043',  4, 410, 'completed', 'BOG', '**** 7102',  60 * 70),
  mkCashout('co_2044',  5, 130, 'completed', 'TBC', '**** 1284',  60 * 72),
  mkCashout('co_2045', 19, 160, 'completed', 'BOG', '**** 1212',  60 * 75),
  mkCashout('co_2046', 16, 220, 'completed', 'TBC', '**** 7117',  60 * 78 + 12),
  mkCashout('co_2047', 24, 120, 'completed', 'TBC', '**** 0808',  60 * 81),
  mkCashout('co_2048',  3, 95,  'completed', 'BOG', '**** 9999',  60 * 84),
  mkCashout('co_2049',  8, 280, 'completed', 'TBC', '**** 5454',  60 * 88),
  mkCashout('co_2050', 21, 165, 'completed', 'BOG', '**** 1828',  60 * 92),
];

// ── Activity feed (last ~12 events) ────────────────────────────────
export const MOCK_ACTIVITY: ActivityEvent[] = [
  { id: 'ev_001', at: minutesAgo(2),   type: 'cashout_submitted', message: 'Cashout submitted',         amount:  100, driverName: MOCK_DRIVERS[0].name,  severity: 'info'    },
  { id: 'ev_002', at: minutesAgo(4),   type: 'cashout_submitted', message: 'Cashout submitted',         amount:  250, driverName: MOCK_DRIVERS[4].name,  severity: 'info'    },
  { id: 'ev_003', at: minutesAgo(12),  type: 'cashout_failed',    message: 'Cashout failed — review',   amount:   60, driverName: MOCK_DRIVERS[12].name, severity: 'danger'  },
  { id: 'ev_004', at: minutesAgo(25),  type: 'cashout_failed',    message: 'Bank timeout — retried',    amount:   80, driverName: MOCK_DRIVERS[7].name,  severity: 'warning' },
  { id: 'ev_005', at: minutesAgo(35),  type: 'cashout_completed', message: 'Cashout completed',         amount:  410, driverName: MOCK_DRIVERS[15].name, severity: 'success' },
  { id: 'ev_006', at: minutesAgo(48),  type: 'cashout_completed', message: 'Cashout completed',         amount:  200, driverName: MOCK_DRIVERS[2].name,  severity: 'success' },
  { id: 'ev_007', at: minutesAgo(85),  type: 'driver_joined',     message: 'Driver onboarded',                            driverName: MOCK_DRIVERS[11].name, severity: 'info'    },
  { id: 'ev_008', at: minutesAgo(120), type: 'cashout_completed', message: 'Cashout completed',         amount:  290, driverName: MOCK_DRIVERS[16].name, severity: 'success' },
  { id: 'ev_009', at: minutesAgo(180), type: 'float_topped_up',   message: 'Float topped up · BOG',     amount: 25_000,                                  severity: 'success' },
  { id: 'ev_010', at: minutesAgo(260), type: 'cashout_completed', message: 'Cashout completed',         amount:  110, driverName: MOCK_DRIVERS[24].name, severity: 'success' },
];

// ── Hourly volume for sparkline (last 12 hours) ────────────────────
export const MOCK_HOURLY_VOLUME: { hour: string; value: number }[] = [
  { hour: '06', value:  120 },
  { hour: '07', value:  240 },
  { hour: '08', value:  680 },
  { hour: '09', value:  920 },
  { hour: '10', value:  840 },
  { hour: '11', value:  610 },
  { hour: '12', value:  990 },
  { hour: '13', value: 1240 },
  { hour: '14', value: 1110 },
  { hour: '15', value:  870 },
  { hour: '16', value: 1320 },
  { hour: '17', value: 1485 },
];
