/**
 * Field errors from a ValidationProblemDetails body, keyed in one normal form so
 * forms can find them.
 *
 * The API produces two kinds of key:
 * - FluentValidation property paths, PascalCase: "Currency", "Items[0].Sku";
 * - JSON binding errors, as JSON paths: "$.items[0].quantity", plus "request"
 *   for a body that could not be read at all.
 * Both become lower-case paths without the "$." prefix: "currency",
 * "items[0].sku". Messages for keys that collapse to the same path are merged.
 */
export function normalizeFieldErrors(
  errors: Readonly<Record<string, readonly string[]>>,
): ReadonlyMap<string, readonly string[]> {
  const normalized = new Map<string, string[]>();

  for (const [key, messages] of Object.entries(errors)) {
    const path = key.replace(/^\$\.?/, '').toLowerCase();
    normalized.set(path, [...(normalized.get(path) ?? []), ...messages]);
  }

  return normalized;
}
