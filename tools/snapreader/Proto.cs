// Minimal protobuf wire reader shared by the forensic decoders.
static class Proto
{
    public record Field(int Num, int Wire, ulong Varint, byte[]? Bytes);

    public static List<Field> Parse(ReadOnlySpan<byte> v)
    {
        var fields = new List<Field>();
        int i = 0;
        while (i < v.Length)
        {
            if (!TryVarint(v, ref i, out ulong tag)) throw new Exception("bad tag");
            int num = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            switch (wire)
            {
                case 0:
                    if (!TryVarint(v, ref i, out ulong val)) throw new Exception("bad varint");
                    fields.Add(new Field(num, 0, val, null));
                    break;
                case 1:
                    fields.Add(new Field(num, 1, BitConverter.ToUInt64(v.Slice(i, 8)), null));
                    i += 8;
                    break;
                case 2:
                    if (!TryVarint(v, ref i, out ulong len) || i + (int)len > v.Length) throw new Exception("bad len");
                    fields.Add(new Field(num, 2, 0, v.Slice(i, (int)len).ToArray()));
                    i += (int)len;
                    break;
                case 5:
                    fields.Add(new Field(num, 5, BitConverter.ToUInt32(v.Slice(i, 4)), null));
                    i += 4;
                    break;
                default:
                    throw new Exception($"wire {wire}");
            }
        }
        return fields;
    }

    public static ulong V(List<Field> f, int num) => f.FirstOrDefault(x => x.Num == num && x.Wire == 0)?.Varint ?? 0;
    public static long S(List<Field> f, int num) => unchecked((long)V(f, num));
    public static byte[]? B(List<Field> f, int num) => f.FirstOrDefault(x => x.Num == num && x.Wire == 2)?.Bytes;
    public static string Str(List<Field> f, int num) => B(f, num) is { } b ? System.Text.Encoding.UTF8.GetString(b) : "";
    public static IEnumerable<byte[]> All(List<Field> f, int num) => f.Where(x => x.Num == num && x.Wire == 2).Select(x => x.Bytes!);

    static bool TryVarint(ReadOnlySpan<byte> b, ref int i, out ulong val)
    {
        val = 0; int shift = 0;
        while (i < b.Length)
        {
            byte c = b[i++];
            val |= (ulong)(c & 0x7f) << shift;
            if ((c & 0x80) == 0) return true;
            shift += 7;
            if (shift > 63) return false;
        }
        return false;
    }

    public static string Ts(long ms) =>
        ms == 0 ? "-" : DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("HH:mm:ss.fff");
}
