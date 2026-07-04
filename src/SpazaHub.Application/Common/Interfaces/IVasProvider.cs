using SpazaHub.Domain.Enums;

namespace SpazaHub.Application.Common.Interfaces;

public sealed record VasProduct(string ProductCode, VasProductType Type, string Name, decimal Amount);

public sealed record VasVendResult(
    VasTransactionStatus Status,
    string? ProviderReference,
    string? Token,
    string? Error);

/// <summary>
/// Port for the VAS aggregator (Flash, Blu Label, Kazang later; FakeVasProvider now).
/// Every vend carries a client-generated idempotency key. On a lost response the caller
/// re-queries by key and never re-vends.
/// </summary>
public interface IVasProvider
{
    Task<VasVendResult> VendAirtimeAsync(
        Guid idempotencyKey, string productCode, string targetPhone, decimal amount,
        CancellationToken cancellationToken = default);

    Task<VasVendResult> VendElectricityAsync(
        Guid idempotencyKey, string meterNumber, decimal amount,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VasProduct>> QueryProductsAsync(
        VasProductType type, CancellationToken cancellationToken = default);

    Task<VasVendResult?> QueryTransactionByIdempotencyKeyAsync(
        Guid idempotencyKey, CancellationToken cancellationToken = default);
}
