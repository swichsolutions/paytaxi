// Admin-side mock data. Every admin page reads the backend now; the only
// remaining consumer is the hourly-volume fallback in AdminMockService.
// The Phase-5 driver/queue/activity/admin-user fixtures were removed along
// with the last UI that displayed them (mock "Nika Maisuradze" initiator,
// sidebar float widget, float_topped_up activity case).

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
