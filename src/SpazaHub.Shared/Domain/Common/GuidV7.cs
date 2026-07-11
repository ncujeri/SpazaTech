using System.Security.Cryptography;

namespace SpazaHub.Domain.Common;

/// <summary>
/// Generates RFC 9562 version 7 GUIDs: 48-bit Unix millisecond timestamp followed by random bits.
/// Client-generated, time-ordered keys keep SQL Server index fragmentation low and allow
/// records to be created fully offline. .NET 8 has no built-in Guid.CreateVersion7, so this
/// implementation is used everywhere a new primary key is needed.
/// </summary>
public static class GuidV7
{
    /// <summary>Creates a new version 7 GUID using the current UTC time.</summary>
    public static Guid NewGuid() => NewGuid(DateTimeOffset.UtcNow);

    /// <summary>Creates a new version 7 GUID for a specific timestamp. Exposed for tests.</summary>
    public static Guid NewGuid(DateTimeOffset timestamp)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes[6..]);

        long unixMs = timestamp.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(unixMs >> 40);
        bytes[1] = (byte)(unixMs >> 32);
        bytes[2] = (byte)(unixMs >> 24);
        bytes[3] = (byte)(unixMs >> 16);
        bytes[4] = (byte)(unixMs >> 8);
        bytes[5] = (byte)unixMs;

        // Version 7 in the high nibble of byte 6, RFC 4122 variant in the top bits of byte 8.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>Extracts the millisecond Unix timestamp embedded in a version 7 GUID.</summary>
    public static DateTimeOffset GetTimestamp(Guid guid)
    {
        Span<byte> bytes = stackalloc byte[16];
        guid.TryWriteBytes(bytes, bigEndian: true, out _);
        long unixMs = ((long)bytes[0] << 40) | ((long)bytes[1] << 32) | ((long)bytes[2] << 24)
                    | ((long)bytes[3] << 16) | ((long)bytes[4] << 8) | bytes[5];
        return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
    }
}
