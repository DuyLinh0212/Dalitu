import { formatPaymentCountdown } from './payment-countdown';

describe('payment countdown', () => {
  it('shows hours for a one-day payment and minutes for shorter durations', () => {
    expect(formatPaymentCountdown(86400)).toBe('24:00:00');
    expect(formatPaymentCountdown(3661)).toBe('1:01:01');
    expect(formatPaymentCountdown(3599)).toBe('59:59');
    expect(formatPaymentCountdown(9)).toBe('0:09');
  });
});
