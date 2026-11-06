using System.Buffers.Binary;

namespace KeyValueStore;

/// <summary>
/// On-disk format. File = magic + records. Record = crc32(payload) | payloadLength | payload.
/// Payload = opCount | { opType | keyLen | key | (valueLen | value, for puts) }*.
/// Every mutation, even a single put, is one record, so a record is the unit of atomicity.
/// </summary>
internal static class LogFormat
{
    public static readonly byte[] Magic = "KVL1"u8.ToArray();
    public const int RecordHeaderSize = 8;
    public const int MaxRecordSize = 256 * 1024 * 1024;
    public const byte OpPut = 1;
    public const byte OpDelete = 2;

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    public static byte[] EncodeRecord(IReadOnlyList<Operation> ops)
    {
        using var payload = new MemoryStream();
        using (var w = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(ops.Count);
            foreach (var op in ops)
            {
                w.Write(op.IsDelete ? OpDelete : OpPut);
                w.Write(op.Key.Length);
                w.Write(op.Key);
                if (!op.IsDelete)
                {
                    w.Write(op.Value!.Length);
                    w.Write(op.Value);
                }
            }
        }
        var bytes = payload.ToArray();
        var record = new byte[RecordHeaderSize + bytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(record, Crc32(bytes));
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), bytes.Length);
        bytes.CopyTo(record, RecordHeaderSize);
        return record;
    }

    /// <summary>Parses a payload that already passed the CRC check. Returns null if it is structurally invalid.</summary>
    public static List<Operation>? DecodePayload(byte[] payload)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(payload));
            var count = r.ReadInt32();
            if (count < 0 || count > payload.Length) return null;
            var ops = new List<Operation>(count);
            for (var i = 0; i < count; i++)
            {
                var type = r.ReadByte();
                var key = ReadBlob(r, payload.Length);
                if (key is null) return null;
                if (type == OpDelete) ops.Add(new Operation(key, null));
                else if (type == OpPut)
                {
                    var value = ReadBlob(r, payload.Length);
                    if (value is null) return null;
                    ops.Add(new Operation(key, value));
                }
                else return null;
            }
            return r.BaseStream.Position == payload.Length ? ops : null;
        }
        catch (EndOfStreamException) { return null; }
    }

    private static byte[]? ReadBlob(BinaryReader r, int max)
    {
        var len = r.ReadInt32();
        if (len < 0 || len > max) return null;
        var data = r.ReadBytes(len);
        return data.Length == len ? data : null;
    }
}

/// <summary>A single put (Value != null) or delete (Value == null).</summary>
internal readonly record struct Operation(byte[] Key, byte[]? Value)
{
    public bool IsDelete => Value is null;
}
