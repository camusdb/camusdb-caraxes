using System.Text;
using RocksDbSharp;

// Row-revision extractor: dumps every kv-CF entry whose embedded global row index is in the set.
// Value = RocksDbKeyValueMessage: 2=row bytes, 10=lastModifiedPhysical, 12=revision, 13=state.
// CompiledRow: global index int32 @14, balance int64 @26, version int64 @34 (little-endian).
static class Rows
{
    public static void Run(string dbPath, string indexesCsv)
    {
        var wanted = indexesCsv.Split(',').Select(long.Parse).ToHashSet();
        var cfNames = RocksDb.ListColumnFamilies(new DbOptions(), dbPath);
        var cfd = new ColumnFamilies();
        foreach (var n in cfNames) cfd.Add(n, new ColumnFamilyOptions());
        using var db = RocksDb.OpenReadOnly(new DbOptions(), dbPath, cfd, false);
        var h = db.GetColumnFamily("kv");
        using var it = db.NewIterator(h);

        foreach (string ks in new[] { "1:1:r/", "1:2:r/", "1:3:r/", "1:4:r/" })
        {
            byte[] p = Encoding.UTF8.GetBytes(ks);
            it.Seek(p);
            while (it.Valid())
            {
                byte[] k = it.Key();
                if (!k.AsSpan().StartsWith(p)) break;
                try
                {
                    var f = Proto.Parse(it.Value());
                    byte[]? row = Proto.B(f, 2);
                    if (row is { Length: >= 42 })
                    {
                        long idx = BitConverter.ToInt32(row, 14);
                        if (wanted.Contains(idx))
                        {
                            long bal = BitConverter.ToInt64(row, 26);
                            long ver = BitConverter.ToInt64(row, 34);
                            long rev = Proto.S(f, 12);
                            long state = Proto.S(f, 13);
                            string ts = Proto.Ts(Proto.S(f, 10));
                            Console.WriteLine($"{Encoding.UTF8.GetString(k)}\tidx={idx}\tbal={bal}\tver={ver}\trev={rev}\tstate={state}\tlastMod={ts}");
                        }
                    }
                }
                catch { /* non-row entries (floors) or foreign payloads */ }
                it.Next();
            }
        }
    }
}
