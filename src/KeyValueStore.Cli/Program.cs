using KeyValueStore;

// Usage: kvs <file> [command args...]
// Without a command, reads commands from standard input (one per line).
//   set <key> <value> | get <key> | del <key> | keys | stats | compact | quit
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: kvs <file> [set|get|del|keys|stats|compact ...]");
    return 2;
}

using var store = KvStore.Open(args[0]);
if (store.Recovery.BytesTruncated > 0)
    Console.Error.WriteLine($"recovered: dropped {store.Recovery.BytesTruncated} bytes of a torn write");

if (args.Length > 1) return Run(store, args.Skip(1).ToArray()) ? 0 : 1;

string? line;
while ((line = Console.ReadLine()) is not null)
{
    var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) continue;
    if (parts[0] is "quit" or "exit") break;
    Run(store, parts);
}
return 0;

static bool Run(KvStore store, string[] a)
{
    switch (a[0])
    {
        case "set" when a.Length >= 3:
            store.Put(a[1], string.Join(' ', a.Skip(2)));
            Console.WriteLine("OK");
            return true;
        case "get" when a.Length == 2:
            var v = store.GetString(a[1]);
            Console.WriteLine(v ?? "(nil)");
            return v is not null;
        case "del" when a.Length == 2:
            Console.WriteLine(store.Delete(a[1]) ? "1" : "0");
            return true;
        case "keys":
            foreach (var k in store.Keys()) Console.WriteLine(k);
            return true;
        case "stats":
            var s = store.Stats();
            Console.WriteLine($"keys={s.Keys} log_bytes={s.LogBytes} live_bytes={s.LiveBytes} compactions={s.Compactions}");
            return true;
        case "compact":
            store.Compact();
            Console.WriteLine("OK");
            return true;
        default:
            Console.Error.WriteLine("unknown or malformed command");
            return false;
    }
}
