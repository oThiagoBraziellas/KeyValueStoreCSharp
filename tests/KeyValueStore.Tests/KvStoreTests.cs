namespace KeyValueStore.Tests;

public sealed class TempFile : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "kv-" + Guid.NewGuid().ToString("N"));
    public string Path_ => System.IO.Path.Combine(Dir, "data.log");
    public TempFile() => Directory.CreateDirectory(Dir);
    public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
}

public class KvStoreTests
{
    private static readonly KvStoreOptions Fast = new() { Sync = SyncMode.OnClose };

    [Fact]
    public void PutGetDelete()
    {
        using var t = new TempFile();
        using var s = KvStore.Open(t.Path_, Fast);
        s.Put("a", "1");
        Assert.Equal("1", s.GetString("a"));
        s.Put("a", "2");
        Assert.Equal("2", s.GetString("a"));
        Assert.True(s.Delete("a"));
        Assert.False(s.Delete("a"));
        Assert.Null(s.Get("a"));
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void Get_ReturnsCopy()
    {
        using var t = new TempFile();
        using var s = KvStore.Open(t.Path_, Fast);
        s.Put("k", new byte[] { 1, 2, 3 });
        s.Get("k")![0] = 99;
        Assert.Equal(1, s.Get("k")![0]);
    }

    [Fact]
    public void Data_SurvivesReopen_IncludingDeletesAndOverwrites()
    {
        using var t = new TempFile();
        using (var s = KvStore.Open(t.Path_))
        {
            s.Put("keep", "v");
            s.Put("gone", "x");
            s.Put("over", "old");
            s.Put("over", "new");
            s.Delete("gone");
        }
        using var again = KvStore.Open(t.Path_);
        Assert.Equal("v", again.GetString("keep"));
        Assert.Null(again.GetString("gone"));
        Assert.Equal("new", again.GetString("over"));
        Assert.Equal(5, again.Recovery.RecordsReplayed);
        Assert.Equal(0, again.Recovery.BytesTruncated);
    }

    [Fact]
    public void BinaryAndUnicodeKeys_RoundTrip()
    {
        using var t = new TempFile();
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        using (var s = KvStore.Open(t.Path_, Fast)) { s.Put("chave-ção-日本", bytes); s.Put("empty", Array.Empty<byte>()); }
        using var r = KvStore.Open(t.Path_);
        Assert.Equal(bytes, r.Get("chave-ção-日本"));
        Assert.Empty(r.Get("empty")!);
    }

    [Fact]
    public void TornTail_IsTruncated_AndEarlierDataSurvives()
    {
        using var t = new TempFile();
        using (var s = KvStore.Open(t.Path_)) { s.Put("a", "1"); s.Put("b", "2"); s.Put("c", "3"); }
        var full = new FileInfo(t.Path_).Length;
        using (var fs = new FileStream(t.Path_, FileMode.Open)) fs.SetLength(full - 3); // crash mid-write of last record

        using var r = KvStore.Open(t.Path_);
        Assert.Equal("1", r.GetString("a"));
        Assert.Equal("2", r.GetString("b"));
        Assert.Null(r.GetString("c"));
        Assert.True(r.Recovery.BytesTruncated > 0);
        Assert.Equal(2, r.Recovery.RecordsReplayed);
    }

    [Fact]
    public void CorruptedCrc_AtTail_IsDropped()
    {
        using var t = new TempFile();
        using (var s = KvStore.Open(t.Path_)) { s.Put("a", "1"); s.Put("b", "2"); }
        var bytes = File.ReadAllBytes(t.Path_);
        bytes[^1] ^= 0xFF; // flip a bit in the last record's payload
        File.WriteAllBytes(t.Path_, bytes);

        using var r = KvStore.Open(t.Path_);
        Assert.Equal("1", r.GetString("a"));
        Assert.Null(r.GetString("b"));
    }

    [Fact]
    public void AfterRecovery_NewWritesAreAppendedCleanly()
    {
        using var t = new TempFile();
        using (var s = KvStore.Open(t.Path_)) { s.Put("a", "1"); s.Put("b", "2"); }
        using (var fs = new FileStream(t.Path_, FileMode.Open)) fs.SetLength(fs.Length - 2);
        using (var r = KvStore.Open(t.Path_)) { r.Put("c", "3"); }
        using var final = KvStore.Open(t.Path_);
        Assert.Equal(new[] { "a", "c" }, final.Keys());
        Assert.Equal(0, final.Recovery.BytesTruncated);
    }

    [Fact]
    public void GarbageAppendedAfterValidData_IsDiscarded()
    {
        using var t = new TempFile();
        using (var s = KvStore.Open(t.Path_)) s.Put("a", "1");
        using (var fs = new FileStream(t.Path_, FileMode.Append)) fs.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
        using var r = KvStore.Open(t.Path_);
        Assert.Equal("1", r.GetString("a"));
        Assert.Equal(12, r.Recovery.BytesTruncated);
    }

    [Fact]
    public void Batch_IsAtomic_OnCrash()
    {
        using var t = new TempFile();
        using (var s = KvStore.Open(t.Path_))
        {
            s.Put("base", "x");
            s.Write(b => { b.Put("k1", "1"); b.Put("k2", "2"); b.Delete("base"); });
        }
        using (var fs = new FileStream(t.Path_, FileMode.Open)) fs.SetLength(fs.Length - 1);
        using var r = KvStore.Open(t.Path_);
        Assert.Equal("x", r.GetString("base")); // batch not applied at all
        Assert.Null(r.GetString("k1"));
        Assert.Null(r.GetString("k2"));
    }

    [Fact]
    public void Batch_AppliesAllOperations()
    {
        using var t = new TempFile();
        using (var s = KvStore.Open(t.Path_, Fast))
        {
            s.Put("base", "x");
            s.Write(b => { b.Put("k1", "1"); b.Delete("base"); });
        }
        using var r = KvStore.Open(t.Path_);
        Assert.Equal(new[] { "k1" }, r.Keys());
    }

    [Fact]
    public void BadMagic_Throws()
    {
        using var t = new TempFile();
        File.WriteAllBytes(t.Path_, "this is not a log file"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => KvStore.Open(t.Path_));
    }

    [Fact]
    public void Compaction_ShrinksLog_AndPreservesLiveData()
    {
        using var t = new TempFile();
        using var s = KvStore.Open(t.Path_, new KvStoreOptions { Sync = SyncMode.OnClose, AutoCompact = false });
        for (var i = 0; i < 1000; i++) s.Put("hot", new string('x', 100) + i);
        s.Put("cold", "stay");
        s.Put("tmp", "x"); s.Delete("tmp");
        var before = s.Stats().LogBytes;
        s.Compact();
        var after = s.Stats().LogBytes;
        Assert.True(after < before / 50, $"{before} -> {after}");
        Assert.Equal("stay", s.GetString("cold"));
        Assert.EndsWith("999", s.GetString("hot"));
        Assert.Null(s.GetString("tmp"));
        s.Put("after", "compaction"); // log is writable again
        s.Dispose();
        using var r = KvStore.Open(t.Path_);
        Assert.Equal(new[] { "after", "cold", "hot" }, r.Keys());
        Assert.Equal("compaction", r.GetString("after"));
    }

    [Fact]
    public void AutoCompaction_Triggers()
    {
        using var t = new TempFile();
        using var s = KvStore.Open(t.Path_, new KvStoreOptions { Sync = SyncMode.OnClose, AutoCompactMinBytes = 4096, CompactionGarbageRatio = 0.6 });
        for (var i = 0; i < 500; i++) s.Put("k", "value-" + new string('y', 50));
        Assert.True(s.Stats().Compactions > 0);
        Assert.True(s.Stats().LogBytes < 20_000);
        Assert.Equal(1, s.Count);
    }

    [Fact]
    public void LeftoverCompactionTempFile_IsIgnored()
    {
        using var t = new TempFile();
        using (var s = KvStore.Open(t.Path_, Fast)) s.Put("a", "1");
        File.WriteAllText(t.Path_ + ".compact", "partial garbage from a crashed compaction");
        using var r = KvStore.Open(t.Path_);
        Assert.Equal("1", r.GetString("a"));
        Assert.False(File.Exists(t.Path_ + ".compact"));
    }

    [Fact]
    public void ConcurrentWriters_AreSerialisedAndAllDurable()
    {
        using var t = new TempFile();
        using (var s = KvStore.Open(t.Path_, Fast))
            Parallel.For(0, 8, w => { for (var i = 0; i < 200; i++) s.Put($"w{w}-{i}", i.ToString()); });
        using var r = KvStore.Open(t.Path_);
        Assert.Equal(1600, r.Count);
        Assert.Equal("199", r.GetString("w7-199"));
    }

    [Fact]
    public void UseAfterDispose_Throws()
    {
        using var t = new TempFile();
        var s = KvStore.Open(t.Path_, Fast);
        s.Dispose();
        Assert.Throws<ObjectDisposedException>(() => s.Put("a", "b"));
    }
}

public class LogFormatTests
{
    [Fact]
    public void Crc32_MatchesKnownVector() => Assert.Equal(0xCBF43926u, LogFormat.Crc32("123456789"u8));

    [Fact]
    public void EncodeDecode_RoundTrip()
    {
        var ops = new[] { new Operation("k"u8.ToArray(), "v"u8.ToArray()), new Operation("d"u8.ToArray(), null) };
        var rec = LogFormat.EncodeRecord(ops);
        var decoded = LogFormat.DecodePayload(rec[LogFormat.RecordHeaderSize..])!;
        Assert.Equal(2, decoded.Count);
        Assert.True(decoded[1].IsDelete);
        Assert.Equal("v"u8.ToArray(), decoded[0].Value);
    }

    [Fact]
    public void DecodePayload_RejectsGarbage() => Assert.Null(LogFormat.DecodePayload(new byte[] { 5, 0, 0, 0, 9 }));
}
