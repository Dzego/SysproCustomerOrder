/**
 * Exact money arithmetic for display and form previews.
 *
 * Amounts are decimal(18,2) on the server. A JavaScript number cannot hold
 * every such value exactly, and products such as 0.1 × 3 drift
 * (0.30000000000000004). Amounts are therefore converted to whole cents as
 * BigInt, multiplied and added exactly, and formatted from the integer.
 */

const AMOUNT = /^(\d+)(?:\.(\d{1,2}))?$/;

/**
 * Whole cents of a non-negative amount with at most two decimals, or null if
 * the value is not such an amount. Accepts numbers and the strings a form holds.
 */
export function toCents(amount: number | string): bigint | null {
  const text = typeof amount === 'number' ? numberToPlainString(amount) : amount.trim();

  if (text === null) {
    return null;
  }

  const match = AMOUNT.exec(text);

  if (!match) {
    return null;
  }

  const [, whole, fraction = ''] = match;

  return BigInt(whole) * 100n + BigInt(fraction.padEnd(2, '0'));
}

/** UnitPrice × Quantity in cents, as the API calculates LineTotal. */
export function lineTotalCents(unitPrice: number | string, quantity: number): bigint | null {
  const cents = toCents(unitPrice);

  if (cents === null || !Number.isSafeInteger(quantity) || quantity < 0) {
    return null;
  }

  return cents * BigInt(quantity);
}

export function sumCents(values: readonly bigint[]): bigint {
  return values.reduce((sum, value) => sum + value, 0n);
}

const WHOLE = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 });

/** "1,234.50", formatted from the integer, so no digit is lost to rounding. */
export function formatCents(cents: bigint): string {
  const negative = cents < 0n;
  const absolute = negative ? -cents : cents;
  const whole = WHOLE.format(absolute / 100n);
  const fraction = (absolute % 100n).toString().padStart(2, '0');

  return `${negative ? '-' : ''}${whole}.${fraction}`;
}

/** Formats an amount the API returned, for example a total. */
export function formatAmount(amount: number): string {
  const cents = toCents(Math.abs(amount));

  if (cents === null) {
    // More than two decimals cannot come from decimal(18,2); show it as is.
    return String(amount);
  }

  return formatCents(amount < 0 ? -cents : cents);
}

/**
 * The digits of a finite number without exponent notation, or null. Numbers the
 * API returns for decimal(18,2) have at most two decimals; anything with more is
 * rejected by AMOUNT rather than silently rounded.
 */
function numberToPlainString(value: number): string | null {
  if (!Number.isFinite(value) || value < 0) {
    return null;
  }

  const text = String(value);

  if (!text.includes('e')) {
    return text;
  }

  // Large whole numbers such as 1e21 print with an exponent.
  return Number.isInteger(value) ? BigInt(value).toString() : null;
}
