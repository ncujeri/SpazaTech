using SpazaHub.Domain.Common;

namespace SpazaHub.Domain.Tests;

public class GuidV7Tests
{
    [Fact]
    public void NewGuid_SetsVersion7AndRfcVariant()
    {
        Guid guid = GuidV7.NewGuid();

        Span<byte> bytes = stackalloc byte[16];
        Assert.True(guid.TryWriteBytes(bytes, bigEndian: true, out _));

        Assert.Equal(0x70, bytes[6] & 0xF0);
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }

    [Fact]
    public void NewGuid_EmbedsTimestamp()
    {
        var timestamp = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

        Guid guid = GuidV7.NewGuid(timestamp);

        Assert.Equal(timestamp, GuidV7.GetTimestamp(guid));
    }

    [Fact]
    public void NewGuid_LaterTimestampsSortHigher()
    {
        var early = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var late = early.AddMilliseconds(5);

        // GUID v7 ordering is by big-endian byte sequence.
        Guid earlyGuid = GuidV7.NewGuid(early);
        Guid lateGuid = GuidV7.NewGuid(late);

        Span<byte> earlyBytes = stackalloc byte[16];
        Span<byte> lateBytes = stackalloc byte[16];
        earlyGuid.TryWriteBytes(earlyBytes, bigEndian: true, out _);
        lateGuid.TryWriteBytes(lateBytes, bigEndian: true, out _);

        Assert.True(earlyBytes[..6].SequenceCompareTo(lateBytes[..6]) < 0);
    }

    [Fact]
    public void NewGuid_IsUnique()
    {
        var guids = Enumerable.Range(0, 10_000).Select(_ => GuidV7.NewGuid()).ToHashSet();

        Assert.Equal(10_000, guids.Count);
    }
}
