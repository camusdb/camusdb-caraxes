using System.Text;
using RocksDbSharp;

// Standalone forensic reader for a copied Kahuna node data dir.
// Modes:
//   cfs  <dbPath>
//   scan <dbPath> <cf> [prefix] [max]
//   get  <dbPath> <cf> <key|hex:...>
//   rows <kvDbPath> <indexesCsv>          - row revisions for global indexes
//   wal  <walDbPath> <keySubstrCsv>       - kv-type raft entries touching keys
//   records <snapshotDir> [txnPhysCsv]    - transaction record snapshots (filter by txn physical ms)
//   walrec <walDbPath> [txnPhysCsv]       - txnrecord-type raft entries
switch (args[0])
{
    case "rows": Rows.Run(args[1], args[2]); return;
    case "wal": Wal.RunKv(args[1], args[2]); return;
    case "walrec": Wal.RunRecords(args[1], args.Length > 2 ? args[2] : ""); return;
    case "records": Records.Run(args[1], args.Length > 2 ? args[2] : ""); return;
}

string mode = args[0];
string dbPath = args[1];

var cfNames = RocksDb.ListColumnFamilies(new DbOptions(), dbPath);
if (mode == "cfs")
{
    foreach (var n in cfNames) Console.WriteLine(n);
    return;
}

var cfd = new ColumnFamilies();
foreach (var n in cfNames) cfd.Add(n, new ColumnFamilyOptions());
using var db = RocksDb.OpenReadOnly(new DbOptions(), dbPath, cfd, false);

if (mode == "scan")
{
    string cf = args[2];
    string prefix = args.Length > 3 ? args[3] : "";
    int max = args.Length > 4 ? int.Parse(args[4]) : 50;
    var h = db.GetColumnFamily(cf);
    using var it = db.NewIterator(h);
    byte[] p = Encoding.UTF8.GetBytes(prefix);
    if (p.Length > 0) it.Seek(p); else it.SeekToFirst();
    int n = 0;
    while (it.Valid() && n < max)
    {
        byte[] k = it.Key();
        if (p.Length > 0 && !k.AsSpan().StartsWith(p)) break;
        Console.WriteLine($"{Util.Printable(k)}  vlen={it.Value().Length}");
        it.Next(); n++;
    }
    return;
}

if (mode == "get")
{
    string cf = args[2];
    string key = args[3];
    var h = db.GetColumnFamily(cf);
    byte[] kb = key.StartsWith("hex:") ? Convert.FromHexString(key[4..]) : Encoding.UTF8.GetBytes(key);
    byte[]? v = db.Get(kb, h);
    Console.WriteLine(v is null ? "(null)" : Convert.ToHexString(v));
}

static class Util
{
    public static string Printable(byte[] b)
    {
        var sb = new StringBuilder();
        foreach (byte c in b)
            sb.Append(c >= 0x20 && c < 0x7f ? (char)c : $"\\x{c:x2}");
        return sb.ToString();
    }
}
