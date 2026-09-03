using System.Text;
using RocksDbSharp;

// Raft WAL decoders. Key = f"{pid:010d}:{logId:020d}", value = RaftLogMessage:
//   1=partition 2=id 3=term 4=type(1=Committed) 5=logType 6=payload 8=timePhysical.
static class Wal
{
    static IEnumerable<(int Pid, long Id, long Term, int Type, string LogType, byte[] Payload, long TimeMs)> Entries(string dbPath)
    {
        var cfNames = RocksDb.ListColumnFamilies(new DbOptions(), dbPath);
        var cfd = new ColumnFamilies();
        foreach (var n in cfNames) cfd.Add(n, new ColumnFamilyOptions());
        using var db = RocksDb.OpenReadOnly(new DbOptions(), dbPath, cfd, false);
        for (int shard = 0; shard < 8; shard++)
        {
            var h = db.GetColumnFamily($"shard{shard}");
            using var it = db.NewIterator(h);
            it.SeekToFirst();
            while (it.Valid())
            {
                var f = Proto.Parse(it.Value());
                string keyStr = Encoding.UTF8.GetString(it.Key());
                int pid = int.Parse(keyStr.AsSpan(0, 10));
                long id = long.Parse(keyStr.AsSpan(11));
                yield return (pid, id, Proto.S(f, 3), (int)Proto.V(f, 4), Proto.Str(f, 5), Proto.B(f, 6) ?? [], Proto.S(f, 8));
                it.Next();
            }
        }
    }

    // kv-type entries whose key contains any of the given substrings.
    public static void RunKv(string dbPath, string keySubstrCsv)
    {
        string[] subs = keySubstrCsv.Split(',');
        foreach (var e in Entries(dbPath))
        {
            if (e.LogType != "kv") continue;
            var kv = Proto.Parse(e.Payload);
            string key = Proto.Str(kv, 2);
            if (!subs.Any(key.Contains)) continue;
            long rev = Proto.S(kv, 4);
            int reqType = (int)Proto.V(kv, 1);
            long txnPhys = Proto.S(kv, 20);
            ulong txnCtr = Proto.V(kv, 21);
            ulong txnNode = Proto.V(kv, 19);
            string anchor = Proto.Str(kv, 22);
            byte[]? val = Proto.B(kv, 3);
            long bal = 0, ver = -1;
            if (val is { Length: >= 42 })
            {
                bal = BitConverter.ToInt64(val, 26);
                ver = BitConverter.ToInt64(val, 34);
            }
            Console.WriteLine($"p{e.Pid} log={e.Id} term={e.Term} rt={e.Type} t={Proto.Ts(e.TimeMs)} req={reqType} key={key} rev={rev} bal={bal} ver={ver} txn={txnNode}:{txnPhys}:{txnCtr} anchor={anchor}");
        }
    }

    // txnrecord-type entries: TransactionRecordDeltaMessage { repeated commands=1 }.
    // Command: 1=kind(0 INIT,1 COMMIT,2 ABORT,3 PURGE) 2/3/4=txn N/L/C 5=epoch 13=coordinatorKey
    //          14=recordAnchorKey 24=participants{key=1} 25=abortClass 26=bundledPrepareKeys.
    public static void RunRecords(string dbPath, string txnPhysCsv)
    {
        var wanted = txnPhysCsv.Length > 0 ? txnPhysCsv.Split(',').Select(long.Parse).ToHashSet() : null;
        foreach (var e in Entries(dbPath))
        {
            if (e.LogType != "txnrecord") continue;
            var delta = Proto.Parse(e.Payload);
            foreach (byte[] cmdBytes in Proto.All(delta, 1))
            {
                var c = Proto.Parse(cmdBytes);
                long phys = Proto.S(c, 3);
                if (wanted is not null && !wanted.Contains(phys)) continue;
                int kind = (int)Proto.V(c, 1);
                string kindName = kind switch { 0 => "INIT", 1 => "COMMIT", 2 => "ABORT", 3 => "PURGE", _ => kind.ToString() };
                var parts = Proto.All(c, 24).Select(p => Proto.Str(Proto.Parse(p), 1)).ToList();
                var bundled = c.Where(x => x.Num == 26 && x.Wire == 2).Select(x => Encoding.UTF8.GetString(x.Bytes!)).ToList();
                Console.WriteLine($"p{e.Pid} log={e.Id} term={e.Term} rt={e.Type} t={Proto.Ts(e.TimeMs)} {kindName} txn={Proto.V(c, 2)}:{phys}:{Proto.V(c, 4)} epoch={Proto.S(c, 5)} anchor={Proto.Str(c, 14)} abortClass={Proto.V(c, 25)} parts=[{string.Join("|", parts)}] bundled=[{string.Join("|", bundled)}]");
            }
        }
    }
}
