namespace SpazaHub.Domain.Enums;

/// <summary>Tender types accepted on a sale. One sale can mix several.</summary>
public enum PaymentMethod
{
    Cash = 0,
    Card = 1,
    SassaCard = 2,
    Qr = 3,
    StoreCredit = 4
}

/// <summary>Reasons stock quantity changes. On-hand stock is always SUM of movements.</summary>
public enum StockMovementType
{
    Sale = 0,
    GoodsReceived = 1,
    Adjustment = 2,
    StockTakeCorrection = 3,
    Wastage = 4
}

/// <summary>Physical cash drawer movements used by cash-up reconciliation.</summary>
public enum CashMovementType
{
    OpeningFloat = 0,
    CashSale = 1,
    CashRefund = 2,
    CashbackPaid = 3,
    CashbackFeeReceived = 4,
    Payout = 5,
    Expense = 6,
    BankDrop = 7,
    RoundingDifference = 8,
    CreditPaymentReceived = 9
}

/// <summary>Direction of a Makhulu Book ledger entry. Balance is always derived, never stored.</summary>
public enum CreditEntryType
{
    Debit = 0,
    Credit = 1
}

/// <summary>Sources of service income, kept separate from goods margin in reports.</summary>
public enum FeeIncomeType
{
    Cashback = 0,
    Vas = 1
}

/// <summary>How the cashback fee is rounded after applying the fee rate.</summary>
public enum FeeRoundingMode
{
    NearestRand = 0,
    UpToRand = 1,
    Exact = 2
}

/// <summary>Delivery channel for a customer message.</summary>
public enum MessageChannel
{
    /// <summary>Tier 1: sms deep link sent from the owner's own phone. Zero cost.</summary>
    OwnerPhoneSms = 0,

    /// <summary>Tier 2: server-side send via the SMS aggregator.</summary>
    ProviderSms = 1
}

/// <summary>Lifecycle of a customer message.</summary>
public enum MessageStatus
{
    Queued = 0,
    Submitted = 1,
    Delivered = 2,
    Failed = 3
}

/// <summary>Lifecycle of a VAS vend. The till counts it only once Confirmed.</summary>
public enum VasTransactionStatus
{
    Pending = 0,
    Confirmed = 1,
    Failed = 2
}

/// <summary>Type of value-added service product.</summary>
public enum VasProductType
{
    Airtime = 0,
    Data = 1,
    Electricity = 2
}

/// <summary>How a scanned barcode maps onto a product and its sale quantity.</summary>
public enum BarcodeKind
{
    /// <summary>One scan is one sellable unit at the product's price. The common case.</summary>
    Unit = 0,

    /// <summary>One scan is a case/carton: it moves UnitsPerScan sellable units at once.</summary>
    Pack = 1,

    /// <summary>
    /// A variable-measure (scale-printed) label whose barcode carries the price for that
    /// one item. Code holds the item-reference digits; the price is read from the scan.
    /// </summary>
    PriceEmbedded = 2
}
