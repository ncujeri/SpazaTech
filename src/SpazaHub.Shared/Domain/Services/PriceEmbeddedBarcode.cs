namespace SpazaHub.Domain.Services;

/// <summary>
/// Reads GS1 "variable measure" barcodes — the labels a shop's own scale or price gun
/// prints for repacked goods (meat, produce), where the barcode encodes the price of that
/// one item rather than identifying a fixed product.
///
/// These are EAN-13 codes in the restricted-distribution range (first digit 2). The last
/// six digits are a five-digit price field followed by the EAN check digit; everything
/// before that is the constant item reference, the same on every label for that product.
/// The price is held in cents, so a field of 03990 means R39.90.
///
/// Field layouts differ between chains, so matching is done by the constant prefix rather
/// than by fixed positions: two labels for the same product share every digit except the
/// price and check digit.
/// </summary>
public static class PriceEmbeddedBarcode
{
    private const int PriceDigits = 5;
    private const int TrailingDigits = PriceDigits + 1; // price field + EAN check digit

    /// <summary>
    /// True when <paramref name="scanned"/> looks like a variable-measure in-store label:
    /// a 13-digit numeric EAN whose first digit is 2 (GS1 restricted distribution).
    /// </summary>
    public static bool IsVariableMeasure(string? scanned)
        => scanned is { Length: 13 } && scanned[0] == '2' && scanned.All(char.IsDigit);

    /// <summary>
    /// The constant item-reference portion of a variable-measure label — the code minus its
    /// price and check digit — used as the stored match key. Returns null if the scan is not
    /// a variable-measure code.
    /// </summary>
    public static string? ItemReferenceOf(string? scanned)
        => IsVariableMeasure(scanned) ? scanned![..^TrailingDigits] : null;

    /// <summary>
    /// Reads the embedded price (in rands) from a variable-measure label. Returns false and
    /// zero when the scan is not such a code or the price field is not numeric.
    /// </summary>
    public static bool TryReadPrice(string? scanned, out decimal priceRands)
    {
        priceRands = 0m;
        if (!IsVariableMeasure(scanned))
        {
            return false;
        }

        string priceField = scanned!.Substring(scanned.Length - TrailingDigits, PriceDigits);
        if (!int.TryParse(priceField, out int cents))
        {
            return false;
        }

        priceRands = cents / 100m;
        return true;
    }
}
