using System.Buffers.Binary;
using System.Text;

namespace KeyValueStore;

public enum SyncMode
{
    /// <summary>fsync after every write: durable against power loss, slowest.</summary>
    Always,
    /// <summary>Data is handed to the OS only (flush); survives process crashes but not necessarily power loss.</summary>
    OnClose
}

public sealed record KvStoreOptions
{
    public SyncMode Sync { get; init; } = SyncMode.Always;
    /// <summary>Compact automatically when the log is at least this big and garbage exceeds <see cref="CompactionGarbageRatio"/>.</summary>
    public long AutoCompactMinBytes { get; init; } = 1024 * 1024;
    public double CompactionGarbageRatio { get; init; } = 0.5;
    public bool AutoCompact { get; init; } = true;
}

/// <summary>What recovery found when the store was opened.</summary>
public sealed record RecoveryInfo(int RecordsReplayed, long BytesTruncated);

public sealed record StoreStats(int Keys, long LogBytes, long LiveBytes, int Compactions);

/// <summary>
/// Durable key-value store built on a write-ahead log. All data lives in memory; the log is the source of truth.
/// Writes are appended (and optionally fsynced) before the in-memory map changes. On open, the log is replayed,
/// and a torn or corrupt tail (e.g. from a crash mid-write) is detected via CRC and truncated.
/// </summary>
public sealed class KvStore : IDisposable
{
    private const int EntryOverhead = 9; // opType + two length prefixes, used for the live-size estimate

    private readonly object _gate = new();
    private readonly string _path;
    private readonly KvStoreOptions _options;
    private readonly Dictionary<string, byte[]> _data = new(StringComparer.Ordinal);
    private FileStream _log;
    private long _liveBytes;
    private int _compactions;
    private bool _disposed;

    private KvStore(string path, KvStoreOptions options, FileStream log, RecoveryInfo recovery)
    {
        _path = path;
        _options = options;
        _log = log;
        Recovery = recovery;
    }

    public RecoveryInfo Recovery { get; }

    public static KvStore Open(string path, KvStoreOptions? options = null)
    {
        options ??= new KvStoreOptions();
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // A leftover temp file means a compaction crashed before the atomic rename: the old log is still intact.
        var temp = full + ".compact";
        if (File.Exists(temp)) File.Delete(temp);

        var fs = new FileStream(full, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.None);
        try
        {
            if (fs.Length == 0)
            {
                fs.Write(LogFormat.Magic);
                fs.Flush(true);
            }
            var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var recovery = Replay(fs, data);
            var store = new KvStore(full, options, fs, recovery);
            foreach (var (k, v) in data) store._data[k] = v;
            store._liveBytes = data.Sum(p => LiveSize(p.Key, p.Value));
            return store;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public int Count { get { lock (_gate) return _data.Count; } }

    public bool ContainsKey(string key) { lock (_gate) return _data.ContainsKey(key); }

    public byte[]? Get(string key)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _data.TryGetValue(key, out var v) ? (byte[])v.Clone() : null;
        }
    }

    public string? GetString(string key) => Get(key) is { } b ? Encoding.UTF8.GetString(b) : null;

    public void Put(string key, byte[] value) => Apply(new[] { new Operation(Encode(key), (byte[])value.Clone()) });

    public void Put(string key, string value) => Put(key, Encoding.UTF8.GetBytes(value));

    /// <summary>Returns true if the key existed.</summary>
    public bool Delete(string key)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_data.ContainsKey(key)) return false;
            Apply(new[] { new Operation(Encode(key), null) });
            return true;
        }
    }

    /// <summary>Applies several puts/deletes as one atomic, durable unit: after a crash either all or none are visible.</summary>
    public void Write(Action<IBatch> build)
    {
        var batch = new Batch();
        build(batch);
        if (batch.Ops.Count > 0) Apply(batch.Ops);
    }

    public IReadOnlyList<string> Keys()
    {
        lock (_gate) return _data.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    public StoreStats Stats()
    {
        lock (_gate) return new StoreStats(_data.Count, _log.Length, _liveBytes + LogFormat.Magic.Length, _compactions);
    }

    /// <summary>
    /// Rewrites the log with only live entries: write temp file, fsync, atomically rename over the old log.
    /// A crash at any step leaves either the old or the new complete log.
    /// </summary>
    public void Compact()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var temp = _path + ".compact";
            using (var tmp = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                tmp.Write(LogFormat.Magic);
                foreach (var (k, v) in _data.OrderBy(p => p.Key, StringComparer.Ordinal))
                    tmp.Write(LogFormat.EncodeRecord(new[] { new Operation(Encode(k), v) }));
                tmp.Flush(true);
            }
            _log.Dispose();
            File.Move(temp, _path, overwrite: true);
            _log = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            _log.Seek(0, SeekOrigin.End);
            _compactions++;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _log.Flush(true);
            _log.Dispose();
        }
    }

    private void Apply(IReadOnlyList<Operation> ops)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var record = LogFormat.EncodeRecord(ops);
            _log.Seek(0, SeekOrigin.End);
            var start = _log.Position;
            try
            {
                _log.Write(record);
                if (_options.Sync == SyncMode.Always) _log.Flush(true); else _log.Flush();
            }
            catch
            {
                // Do not leave a partial record behind; the in-memory state was not changed yet.
                try { _log.SetLength(start); } catch { /* best effort */ }
                throw;
            }
            foreach (var op in ops) ApplyToMemory(op);
            MaybeCompact();
        }
    }

    private void ApplyToMemory(Operation op)
    {
        var key = Decode(op.Key);
        if (_data.TryGetValue(key, out var old)) _liveBytes -= LiveSize(key, old);
        if (op.IsDelete) _data.Remove(key);
        else
        {
            _data[key] = op.Value!;
            _liveBytes += LiveSize(key, op.Value!);
        }
    }

    private void MaybeCompact()
    {
        if (!_options.AutoCompact) return;
        var size = _log.Length;
        if (size < _options.AutoCompactMinBytes) return;
        var garbage = 1.0 - (double)(_liveBytes + LogFormat.Magic.Length) / size;
        if (garbage >= _options.CompactionGarbageRatio) Compact();
    }

    private static RecoveryInfo Replay(FileStream fs, Dictionary<string, byte[]> data)
    {
        var magic = new byte[LogFormat.Magic.Length];
        fs.Seek(0, SeekOrigin.Begin);
        if (fs.Read(magic) != magic.Length || !magic.AsSpan().SequenceEqual(LogFormat.Magic))
            throw new InvalidDataException("Not a KeyValueStore log (bad magic).");

        var good = fs.Position;
        var replayed = 0;
        var header = new byte[LogFormat.RecordHeaderSize];
        while (true)
        {
            if (!ReadFully(fs, header)) break;
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (len < 0 || len > LogFormat.MaxRecordSize || fs.Length - fs.Position < len) break;
            var payload = new byte[len];
            if (!ReadFully(fs, payload) || LogFormat.Crc32(payload) != crc) break;
            var ops = LogFormat.DecodePayload(payload);
            if (ops is null) break;
            foreach (var op in ops)
            {
                var key = Decode(op.Key);
                if (op.IsDelete) data.Remove(key); else data[key] = op.Value!;
            }
            replayed++;
            good = fs.Position;
        }

        var truncated = fs.Length - good;
        if (truncated > 0)
        {
            fs.SetLength(good);
            fs.Flush(true);
        }
        fs.Seek(0, SeekOrigin.End);
        return new RecoveryInfo(replayed, truncated);
    }

    private static bool ReadFully(Stream s, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = s.Read(buffer, read, buffer.Length - read);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private static long LiveSize(string key, byte[] value) =>
        Encoding.UTF8.GetByteCount(key) + value.Length + EntryOverhead + LogFormat.RecordHeaderSize + 4;

    private static byte[] Encode(string key) => Encoding.UTF8.GetBytes(key);
    private static string Decode(byte[] key) => Encoding.UTF8.GetString(key);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class Batch : IBatch
    {
        public List<Operation> Ops { get; } = new();
        public void Put(string key, byte[] value) => Ops.Add(new Operation(Encode(key), (byte[])value.Clone()));
        public void Put(string key, string value) => Ops.Add(new Operation(Encode(key), Encoding.UTF8.GetBytes(value)));
        public void Delete(string key) => Ops.Add(new Operation(Encode(key), null));
    }
}

public interface IBatch
{
    void Put(string key, byte[] value);
    void Put(string key, string value);
    void Delete(string key);
}
