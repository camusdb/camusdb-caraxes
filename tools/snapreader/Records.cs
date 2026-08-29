using System.Text;

// transactionrecord_v1_p*.snapshot decoder. File = TransactionRecordSnapshotMessage
// { repeated TransactionRecordSnapshotEntry records = 1 }.
// Entry: 1/2/3 txn N/L/C, 4 epoch, 5 coordinatorKey, 6 recordAnchorKey, 13 manifestHash,
//        14 participants{key=1,durability=2}, 16 decision(0 UNDECIDED,1 COMMIT,2 ABORT),
//        17 abortClass, 21/22/23 createdAt, 24/25/26 decidedAt.
static class Records
{
    public static void Run(string dir, string txnPhysCsv)
    {
        var wanted = txnPhysCsv.Length > 0 ? txnPhysCsv.Split(',').Select(long.Parse).ToHashSet() : null;
        foreach (string file in Directory.GetFiles(dir, "transactionrecord_*.snapshot").OrderBy(x => x))
        {
            byte[] bytes = File.ReadAllBytes(file);
            if (bytes.Length == 0) continue;
            var msg = Proto.Parse(bytes);
            foreach (byte[] entry in Proto.All(msg, 1))
            {
                var r = Proto.Parse(entry);
                long phys = Proto.S(r, 2);
                if (wanted is not null && !wanted.Contains(phys)) continue;
                int decision = (int)Proto.V(r, 16);
                string d = decision switch { 0 => "UNDECIDED", 1 => "COMMIT", 2 => "ABORT", _ => decision.ToString() };
                var parts = Proto.All(r, 14).Select(p => Proto.Str(Proto.Parse(p), 1)).ToList();
                Console.WriteLine($"{Path.GetFileName(file)} txn={Proto.V(r, 1)}:{phys}:{Proto.V(r, 3)} epoch={Proto.S(r, 4)} {d} abortClass={Proto.V(r, 17)} anchor={Proto.Str(r, 6)} createdAt={Proto.Ts(Proto.S(r, 22))} decidedAt={Proto.Ts(Proto.S(r, 25))} parts=[{string.Join("|", parts)}]");
            }
        }
    }
}
