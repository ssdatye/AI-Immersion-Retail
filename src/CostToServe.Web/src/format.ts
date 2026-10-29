export const usd = (value: number | null | undefined, digits = 2) =>
  value == null
    ? "-"
    : value.toLocaleString("en-US", { style: "currency", currency: "USD", minimumFractionDigits: digits, maximumFractionDigits: digits });

/** Compact USD for large figures: $28.2M, $344.6K. Sign is preserved. */
export function usdCompact(value: number | null | undefined) {
  if (value == null) {
    return "-";
  }
  const abs = Math.abs(value);
  const sign = value < 0 ? "-" : "";
  if (abs >= 1e6) {
    return `${sign}$${(abs / 1e6).toFixed(1)}M`;
  }
  if (abs >= 1e3) {
    return `${sign}$${(abs / 1e3).toFixed(1)}K`;
  }
  return `${sign}$${abs.toFixed(0)}`;
}

export const pct = (value: number | null | undefined, digits = 1) => (value == null ? "-" : `${value.toFixed(digits)}%`);

export const signed = (value: number, digits = 2) => `${value >= 0 ? "+" : "-"}${Math.abs(value).toFixed(digits)}`;

export const share = (value: number) => `${Math.round(value * 100)}%`;

/** A fraction as a percent number, rounded to 0.1 (0.045 -> 4.5). */
export const toPercent = (fraction: number) => Math.round(fraction * 1000) / 10;
