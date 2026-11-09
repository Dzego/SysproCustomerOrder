import { formatAmount, formatCents, lineTotalCents, sumCents, toCents } from './money';

describe('money', () => {
  describe('toCents', () => {
    it.each([
      [0, 0n],
      [19.99, 1999n],
      [0.1, 10n],
      [5, 500n],
      ['12.5', 1250n],
      [' 7.05 ', 705n],
      [1e21, 100000000000000000000000n],
    ])('converts %p to %p cents', (input, expected) => {
      // Act + Assert
      expect(toCents(input)).toBe(expected);
    });

    it.each([
      [-1],
      [1.005],
      ['1.234'],
      ['abc'],
      [''],
      [Number.NaN],
      [Number.POSITIVE_INFINITY],
      [1e-7],
    ])('rejects %p', (input) => {
      expect(toCents(input)).toBeNull();
    });
  });

  it('multiplies price and quantity exactly, where floating point would drift', () => {
    // Arrange: 0.1 * 3 is 0.30000000000000004 as a JavaScript number.
    // Act
    const cents = lineTotalCents(0.1, 3);

    // Assert
    expect(cents).toBe(30n);
    expect(formatCents(cents!)).toBe('0.30');
  });

  it('keeps every digit of totals larger than a double can hold exactly', () => {
    // Arrange: 999,999,999,999.99 × 2,147,483,647 exceeds Number.MAX_SAFE_INTEGER cents.
    const line = lineTotalCents(999_999_999_999.99, 2_147_483_647)!;

    // Act + Assert
    expect(formatCents(line)).toBe('2,147,483,646,999,978,525,163.53');
  });

  it('rejects invalid quantities', () => {
    expect(lineTotalCents(1, 1.5)).toBeNull();
    expect(lineTotalCents(1, -1)).toBeNull();
    expect(lineTotalCents('x', 1)).toBeNull();
  });

  it('sums cents and formats with thousands separators', () => {
    expect(formatCents(sumCents([1999n, 1n, 100000n]))).toBe('1,020.00');
    expect(formatCents(sumCents([]))).toBe('0.00');
    expect(formatCents(-1505n)).toBe('-15.05');
  });

  it('formats API amounts, including negative ones', () => {
    expect(formatAmount(59.97)).toBe('59.97');
    expect(formatAmount(1234567.5)).toBe('1,234,567.50');
    expect(formatAmount(-3.2)).toBe('-3.20');
    expect(formatAmount(1.234)).toBe('1.234');
  });
});
