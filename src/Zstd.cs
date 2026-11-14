using System;
using System.Threading;

namespace SessionPurge
{
    // Zstandard frame decoder (RFC 8878) for compressed Codex rollouts. No dictionaries; every
    // size, offset and table is validated and any inconsistency is refused.
    internal static class Zstd
    {
        private const int BlockMax = 128 * 1024;
        // True when the data starts with a Zstandard frame or a skippable frame.
        internal static bool IsFrame(byte[] data)
        {
            return data.Length >= 4 && (data[0] == 0x28 && data[1] == 0xB5 && data[2] == 0x2F && data[3] == 0xFD || (data[0] & 0xF0) == 0x50 && data[1] == 0x2A && data[2] == 0x4D && data[3] == 0x18);
        }

        internal static byte[] Decompress(byte[] source, int limit, CancellationToken cancellation)
        {
            var output = new Output(limit);
            int position = 0, frames = 0;
            if (!IsFrame(source)) throw Corrupt();
            while (position < source.Length)
            {
                cancellation.ThrowIfCancellationRequested();
                uint magic = (uint)Le(source, ref position, 4, source.Length);
                if ((magic & 0xFFFFFFF0u) == 0x184D2A50u)
                {
                    long skip = Le(source, ref position, 4, source.Length);
                    if (skip > source.Length - position) throw Corrupt();
                    position += (int)skip;
                    continue;
                }
                if (magic != 0xFD2FB528u) throw Corrupt();
                new Frame(source, output).Decode(ref position, cancellation);
                frames++;
            }
            if (frames == 0) throw Corrupt();
            return output.ToArray();
        }

        private static SafetyException Corrupt() { return new SafetyException("The compressed rollout is not a valid Zstandard stream."); }

        private static long Le(byte[] data, ref int position, int count, int end)
        {
            if (count > end - position) throw Corrupt();
            long value = 0;
            for (int i = 0; i < count; i++) value |= (long)data[position + i] << (8 * i);
            position += count;
            return value;
        }

        private static int HighBit(uint value)
        {
            int bit = -1;
            while (value != 0) { value >>= 1; bit++; }
            return bit;
        }

        private sealed class Output
        {
            internal byte[] Buffer = new byte[65536];
            internal int Length;
            private readonly int limit;
            internal Output(int maximum) { limit = maximum; }
            internal void Reserve(long extra)
            {
                if (extra < 0 || Length + extra > limit) throw new SafetyException("The compressed rollout expands beyond the supported size.");
                if (Length + extra <= Buffer.Length) return;
                long size = Math.Max((long)Buffer.Length * 2, Length + extra);
                var grown = new byte[Math.Min(size, limit)];
                Array.Copy(Buffer, grown, Length);
                Array.Clear(Buffer, 0, Length);
                Buffer = grown;
            }
            internal byte[] ToArray()
            {
                var result = new byte[Length];
                Array.Copy(Buffer, result, Length);
                Array.Clear(Buffer, 0, Length);
                return result;
            }
        }

        private sealed class FseTable
        {
            internal int Log;
            internal byte[] Symbol;
            internal byte[] Bits;
            internal ushort[] Base;
        }

        private sealed class Huffman
        {
            internal int MaxBits;
            internal byte[] Symbol;
            internal byte[] Bits;
        }

        // Reads a bitstream backwards from its end, as zstd writes FSE and Huffman streams.
        private sealed class BackBits
        {
            private readonly byte[] data;
            private readonly int start;
            internal long Offset;
            internal BackBits(byte[] source, int begin, int end)
            {
                if (end <= begin || source[end - 1] == 0) throw Corrupt();
                data = source; start = begin;
                Offset = (long)(end - begin) * 8 - (8 - HighBit(source[end - 1]));
            }
            internal ulong Read(int count)
            {
                if (count == 0) return 0;
                Offset -= count;
                long at = Offset;
                int bits = count;
                if (at < 0) { bits += (int)at; at = 0; }
                ulong value = 0;
                if (bits > 0)
                {
                    int index = start + (int)(at >> 3), shift = 0, skip = (int)(at & 7), left = bits;
                    while (left > 0)
                    {
                        ulong mask = left >= 8 ? 0xFFu : (1u << left) - 1;
                        value |= (((ulong)data[index++] >> skip) & mask) << shift;
                        shift += 8 - skip; left -= 8 - skip; skip = 0;
                    }
                    if (left < 0) value &= bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;
                }
                if (Offset < 0) value = -Offset >= 64 ? 0 : value << (int)-Offset;
                return value;
            }
        }

        private sealed class Frame
        {
            private static readonly int[] LiteralBase = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 40, 48, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536 };
            private static readonly int[] LiteralExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
            private static readonly int[] MatchBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 131, 259, 515, 1027, 2051, 4099, 8195, 16387, 32771, 65539 };
            private static readonly int[] MatchExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
            private static readonly short[] LiteralDefault = { 4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1, -1, -1, -1, -1 };
            private static readonly short[] MatchDefault = { 1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1, -1, -1 };
            private static readonly short[] OffsetDefault = { 1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1 };

            private readonly byte[] source;
            private readonly Output output;
            private int frameStart;
            private long window;
            private Huffman huffman;
            private FseTable literalTable, offsetTable, matchTable;
            private readonly long[] repeat = { 1, 4, 8 };
            private byte[] literals = new byte[BlockMax];

            internal Frame(byte[] data, Output target) { source = data; output = target; }

            internal void Decode(ref int position, CancellationToken cancellation)
            {
                int end = source.Length;
                if (position >= end) throw Corrupt();
                int descriptor = source[position++];
                int sizeFlag = descriptor >> 6;
                bool single = (descriptor & 0x20) != 0;
                if ((descriptor & 0x08) != 0) throw Corrupt();
                bool checksum = (descriptor & 0x04) != 0;
                int dictionaryBytes = new[] { 0, 1, 2, 4 }[descriptor & 3];
                if (!single)
                {
                    if (position >= end) throw Corrupt();
                    int windowByte = source[position++];
                    int log = 10 + (windowByte >> 3);
                    if (log > 31) throw Corrupt();
                    long baseSize = 1L << log;
                    window = baseSize + (baseSize >> 3) * (windowByte & 7);
                }
                if (Le(source, ref position, dictionaryBytes, end) != 0) throw new SafetyException("The compressed rollout needs a Zstandard dictionary, which is not supported.");
                int sizeBytes = sizeFlag == 0 ? (single ? 1 : 0) : 1 << sizeFlag;
                long contentSize = -1;
                if (sizeBytes > 0)
                {
                    contentSize = Le(source, ref position, sizeBytes, end);
                    if (sizeBytes == 2) contentSize += 256;
                    if (contentSize < 0) throw Corrupt();
                    output.Reserve(contentSize);
                }
                if (single) { if (contentSize < 0) throw Corrupt(); window = contentSize; }
                frameStart = output.Length;
                int blockLimit = (int)Math.Min(window, BlockMax);
                for (bool last = false; !last; )
                {
                    cancellation.ThrowIfCancellationRequested();
                    int header = (int)Le(source, ref position, 3, end);
                    last = (header & 1) != 0;
                    int type = (header >> 1) & 3, size = header >> 3;
                    if (type == 3 || size > blockLimit) throw Corrupt();
                    int before = output.Length;
                    if (type == 0)
                    {
                        if (size > end - position) throw Corrupt();
                        output.Reserve(size);
                        Array.Copy(source, position, output.Buffer, output.Length, size);
                        output.Length += size; position += size;
                    }
                    else if (type == 1)
                    {
                        if (position >= end) throw Corrupt();
                        output.Reserve(size);
                        byte value = source[position++];
                        for (int i = 0; i < size; i++) output.Buffer[output.Length++] = value;
                    }
                    else
                    {
                        if (size > end - position) throw Corrupt();
                        Block(position, position + size, blockLimit);
                        position += size;
                    }
                    if (output.Length - before > blockLimit) throw Corrupt();
                    if (contentSize >= 0 && output.Length - frameStart > contentSize) throw Corrupt();
                }
                if (contentSize >= 0 && output.Length - frameStart != contentSize) throw Corrupt();
                if (checksum)
                {
                    uint expected = (uint)Le(source, ref position, 4, end);
                    if ((uint)XxHash64(output.Buffer, frameStart, output.Length - frameStart) != expected) throw new SafetyException("The compressed rollout failed its Zstandard checksum.");
                }
                Array.Clear(literals, 0, literals.Length);
            }

            private void Block(int position, int end, int blockLimit)
            {
                int count = Literals(ref position, end, blockLimit);
                if (position >= end) throw Corrupt();
                int sequences = source[position++];
                if (sequences >= 128)
                {
                    if (position >= end) throw Corrupt();
                    if (sequences < 255) sequences = ((sequences - 128) << 8) + source[position++];
                    else
                    {
                        if (end - position < 2) throw Corrupt();
                        sequences = source[position] + (source[position + 1] << 8) + 0x7F00;
                        position += 2;
                    }
                }
                if (sequences == 0)
                {
                    if (position != end) throw Corrupt();
                    Emit(literals, 0, count);
                    return;
                }
                if (position >= end) throw Corrupt();
                int modes = source[position++];
                if ((modes & 3) != 0) throw Corrupt();
                literalTable = Table(modes >> 6, ref position, end, literalTable, LiteralDefault, 6, 35, 9);
                offsetTable = Table((modes >> 4) & 3, ref position, end, offsetTable, OffsetDefault, 5, 31, 8);
                matchTable = Table((modes >> 2) & 3, ref position, end, matchTable, MatchDefault, 6, 52, 9);
                var bits = new BackBits(source, position, end);
                int literalState = (int)bits.Read(literalTable.Log);
                int offsetState = (int)bits.Read(offsetTable.Log);
                int matchState = (int)bits.Read(matchTable.Log);
                if (bits.Offset < 0) throw Corrupt();
                int used = 0;
                for (int i = 0; i < sequences; i++)
                {
                    int offsetCode = offsetTable.Symbol[offsetState], literalCode = literalTable.Symbol[literalState], matchCode = matchTable.Symbol[matchState];
                    if (offsetCode > 31) throw Corrupt();
                    long value = (1L << offsetCode) + (long)bits.Read(offsetCode);
                    long match = MatchBase[matchCode] + (long)bits.Read(MatchExtra[matchCode]);
                    long literal = LiteralBase[literalCode] + (long)bits.Read(LiteralExtra[literalCode]);
                    if (i < sequences - 1)
                    {
                        literalState = literalTable.Base[literalState] + (int)bits.Read(literalTable.Bits[literalState]);
                        matchState = matchTable.Base[matchState] + (int)bits.Read(matchTable.Bits[matchState]);
                        offsetState = offsetTable.Base[offsetState] + (int)bits.Read(offsetTable.Bits[offsetState]);
                    }
                    long distance;
                    if (value > 3)
                    {
                        distance = value - 3;
                        repeat[2] = repeat[1]; repeat[1] = repeat[0]; repeat[0] = distance;
                    }
                    else
                    {
                        long index = value - 1 + (literal == 0 ? 1 : 0);
                        if (index == 0) distance = repeat[0];
                        else
                        {
                            distance = index < 3 ? repeat[index] : repeat[0] - 1;
                            if (index > 1) repeat[2] = repeat[1];
                            repeat[1] = repeat[0];
                            repeat[0] = distance;
                        }
                    }
                    if (literal > count - used) throw Corrupt();
                    Emit(literals, used, (int)literal);
                    used += (int)literal;
                    if (distance <= 0 || distance > output.Length - frameStart || distance > window || match > blockLimit) throw Corrupt();
                    output.Reserve(match);
                    int from = output.Length - (int)distance;
                    byte[] buffer = output.Buffer;
                    int to = output.Length;
                    for (int k = 0; k < match; k++) buffer[to + k] = buffer[from + k];
                    output.Length += (int)match;
                }
                if (bits.Offset != 0) throw Corrupt();
                Emit(literals, used, count - used);
            }

            private void Emit(byte[] data, int start, int count)
            {
                output.Reserve(count);
                Array.Copy(data, start, output.Buffer, output.Length, count);
                output.Length += count;
            }

            private int Literals(ref int position, int end, int blockLimit)
            {
                if (position >= end) throw Corrupt();
                int first = source[position];
                int type = first & 3, format = (first >> 2) & 3;
                int regenerated, compressed = 0, streams = 1;
                if (type < 2)
                {
                    if ((format & 1) == 0) { regenerated = first >> 3; position += 1; }
                    else if (format == 1) { if (end - position < 2) throw Corrupt(); regenerated = (first >> 4) + (source[position + 1] << 4); position += 2; }
                    else { if (end - position < 3) throw Corrupt(); regenerated = (first >> 4) + (source[position + 1] << 4) + (source[position + 2] << 12); position += 3; }
                }
                else
                {
                    int size = format < 2 ? 3 : format == 2 ? 4 : 5;
                    long value = Le(source, ref position, size, end);
                    int width = format < 2 ? 10 : format == 2 ? 14 : 18;
                    regenerated = (int)((value >> 4) & ((1L << width) - 1));
                    compressed = (int)((value >> (4 + width)) & ((1L << width) - 1));
                    streams = format == 0 ? 1 : 4;
                }
                if (regenerated > blockLimit || type >= 2 && streams == 4 && regenerated < 6) throw Corrupt();
                if (type == 0)
                {
                    if (regenerated > end - position) throw Corrupt();
                    Array.Copy(source, position, literals, 0, regenerated);
                    position += regenerated;
                    return regenerated;
                }
                if (type == 1)
                {
                    if (position >= end) throw Corrupt();
                    byte value = source[position++];
                    for (int i = 0; i < regenerated; i++) literals[i] = value;
                    return regenerated;
                }
                if (compressed > end - position) throw Corrupt();
                int stop = position + compressed;
                if (type == 2) huffman = Tree(ref position, stop);
                else if (huffman == null) throw Corrupt();
                if (streams == 1) Stream(position, stop, 0, regenerated);
                else
                {
                    if (stop - position < 6) throw Corrupt();
                    int one = source[position] | source[position + 1] << 8, two = source[position + 2] | source[position + 3] << 8, three = source[position + 4] | source[position + 5] << 8;
                    int start = position + 6;
                    if ((long)one + two + three >= stop - start) throw Corrupt();
                    int segment = (regenerated + 3) / 4;
                    if (3 * segment > regenerated) throw Corrupt();
                    Stream(start, start + one, 0, segment);
                    Stream(start + one, start + one + two, segment, segment);
                    Stream(start + one + two, start + one + two + three, 2 * segment, segment);
                    Stream(start + one + two + three, stop, 3 * segment, regenerated - 3 * segment);
                }
                position = stop;
                return regenerated;
            }

            private void Stream(int start, int end, int target, int count)
            {
                var bits = new BackBits(source, start, end);
                int max = huffman.MaxBits, mask = (1 << max) - 1;
                int state = (int)bits.Read(max);
                int written = 0;
                while (bits.Offset > -max)
                {
                    if (written >= count) throw Corrupt();
                    literals[target + written++] = huffman.Symbol[state];
                    int width = huffman.Bits[state];
                    state = ((state << width) + (int)bits.Read(width)) & mask;
                }
                if (bits.Offset != -max || written != count) throw Corrupt();
            }

            private Huffman Tree(ref int position, int end)
            {
                if (position >= end) throw Corrupt();
                int header = source[position++];
                var weights = new byte[256];
                int count;
                if (header >= 128)
                {
                    count = header - 127;
                    int bytes = (count + 1) / 2;
                    if (bytes > end - position) throw Corrupt();
                    for (int i = 0; i < count; i++) weights[i] = (byte)(i % 2 == 0 ? source[position + i / 2] >> 4 : source[position + i / 2] & 15);
                    position += bytes;
                }
                else
                {
                    if (header == 0 || header > end - position) throw Corrupt();
                    int stop = position + header;
                    int cursor = position;
                    var table = Build(Header(ref cursor, stop, 6, 255), 255);
                    var bits = new BackBits(source, cursor, stop);
                    int one = (int)bits.Read(table.Log), two = (int)bits.Read(table.Log);
                    if (bits.Offset < 0) throw Corrupt();
                    count = 0;
                    while (true)
                    {
                        if (count >= 255) throw Corrupt();
                        weights[count++] = table.Symbol[one];
                        one = table.Base[one] + (int)bits.Read(table.Bits[one]);
                        if (bits.Offset < 0) { if (count >= 255) throw Corrupt(); weights[count++] = table.Symbol[two]; break; }
                        if (count >= 255) throw Corrupt();
                        weights[count++] = table.Symbol[two];
                        two = table.Base[two] + (int)bits.Read(table.Bits[two]);
                        if (bits.Offset < 0) { if (count >= 255) throw Corrupt(); weights[count++] = table.Symbol[one]; break; }
                    }
                    position = stop;
                }
                long total = 0;
                for (int i = 0; i < count; i++)
                {
                    if (weights[i] > 11) throw Corrupt();
                    if (weights[i] > 0) total += 1L << (weights[i] - 1);
                }
                if (total == 0) throw Corrupt();
                int maxBits = HighBit((uint)total) + 1;
                long left = (1L << maxBits) - total;
                if (maxBits > 11 || left <= 0 || (left & (left - 1)) != 0) throw Corrupt();
                weights[count++] = (byte)(HighBit((uint)left) + 1);
                int ones = 0;
                for (int i = 0; i < count; i++) if (weights[i] == 1) ones++;
                if (ones < 2 || ones % 2 != 0) throw Corrupt();
                var lengths = new byte[count];
                for (int i = 0; i < count; i++) lengths[i] = (byte)(weights[i] > 0 ? maxBits + 1 - weights[i] : 0);
                var result = new Huffman { MaxBits = maxBits, Symbol = new byte[1 << maxBits], Bits = new byte[1 << maxBits] };
                var rankCount = new int[maxBits + 1];
                var rankStart = new int[maxBits + 1];
                for (int i = 0; i < count; i++) rankCount[lengths[i]]++;
                int next = 0;
                for (int length = maxBits; length >= 1; length--)
                {
                    rankStart[length] = next;
                    next += rankCount[length] << (maxBits - length);
                }
                if (next != 1 << maxBits) throw Corrupt();
                for (int i = 0; i < count; i++)
                {
                    int length = lengths[i];
                    if (length == 0) continue;
                    int span = 1 << (maxBits - length);
                    for (int k = 0; k < span; k++) { result.Symbol[rankStart[length] + k] = (byte)i; result.Bits[rankStart[length] + k] = (byte)length; }
                    rankStart[length] += span;
                }
                return result;
            }

            private FseTable Table(int mode, ref int position, int end, FseTable previous, short[] defaults, int defaultLog, int maxSymbol, int maxLog)
            {
                if (mode == 0) return Build(new Distribution { Log = defaultLog, Counts = defaults }, maxSymbol);
                if (mode == 1)
                {
                    if (position >= end) throw Corrupt();
                    int symbol = source[position++];
                    if (symbol > maxSymbol) throw Corrupt();
                    return new FseTable { Log = 0, Symbol = new[] { (byte)symbol }, Bits = new byte[1], Base = new ushort[1] };
                }
                if (mode == 2) return Build(Header(ref position, end, maxLog, maxSymbol), maxSymbol);
                if (previous == null) throw Corrupt();
                return previous;
            }

            private sealed class Distribution { internal int Log; internal short[] Counts; }

            private Distribution Header(ref int position, int end, int maxLog, int maxSymbol)
            {
                long bit = (long)position * 8, limit = (long)end * 8;
                Func<int, int> read = width =>
                {
                    if (bit + width > limit) throw Corrupt();
                    int value = 0;
                    for (int i = 0; i < width; i++, bit++) value |= ((source[bit >> 3] >> (int)(bit & 7)) & 1) << i;
                    return value;
                };
                int log = read(4) + 5;
                if (log > maxLog) throw Corrupt();
                var counts = new short[maxSymbol + 1];
                int remaining = 1 << log, symbol = 0;
                while (remaining > 0)
                {
                    if (symbol > maxSymbol) throw Corrupt();
                    int width = HighBit((uint)(remaining + 1)) + 1;
                    int lowMask = (1 << (width - 1)) - 1;
                    int threshold = (1 << width) - 1 - (remaining + 1);
                    int low = read(width - 1);
                    int value;
                    if (low < threshold) value = low;
                    else
                    {
                        value = low | read(1) << (width - 1);
                        if (value > lowMask) value -= threshold;
                    }
                    int probability = value - 1;
                    remaining -= probability < 0 ? -probability : probability;
                    counts[symbol++] = (short)probability;
                    if (probability == 0)
                    {
                        while (true)
                        {
                            int zeros = read(2);
                            for (int i = 0; i < zeros; i++) { if (symbol > maxSymbol) throw Corrupt(); counts[symbol++] = 0; }
                            if (zeros != 3) break;
                        }
                    }
                }
                if (remaining != 0) throw Corrupt();
                position = (int)((bit + 7) >> 3);
                var used = new short[symbol];
                Array.Copy(counts, used, symbol);
                return new Distribution { Log = log, Counts = used };
            }

            private static FseTable Build(Distribution distribution, int maxSymbol)
            {
                int log = distribution.Log, size = 1 << log;
                short[] counts = distribution.Counts;
                if (counts.Length > maxSymbol + 1) throw Corrupt();
                var table = new FseTable { Log = log, Symbol = new byte[size], Bits = new byte[size], Base = new ushort[size] };
                var next = new int[counts.Length];
                int high = size;
                for (int s = 0; s < counts.Length; s++)
                    if (counts[s] == -1) { if (high == 0) throw Corrupt(); table.Symbol[--high] = (byte)s; next[s] = 1; }
                int step = (size >> 1) + (size >> 3) + 3, mask = size - 1, position = 0;
                for (int s = 0; s < counts.Length; s++)
                {
                    if (counts[s] <= 0) continue;
                    next[s] = counts[s];
                    for (int i = 0; i < counts[s]; i++)
                    {
                        table.Symbol[position] = (byte)s;
                        do position = (position + step) & mask; while (position >= high);
                    }
                }
                if (position != 0) throw Corrupt();
                for (int i = 0; i < size; i++)
                {
                    int s = table.Symbol[i];
                    int state = next[s]++;
                    if (state <= 0) throw Corrupt();
                    int bits = log - HighBit((uint)state);
                    table.Bits[i] = (byte)bits;
                    table.Base[i] = (ushort)((state << bits) - size);
                }
                return table;
            }
        }

        internal static ulong XxHash64(byte[] data, int offset, int length)
        {
            const ulong P1 = 11400714785074694791UL, P2 = 14029467366897019727UL, P3 = 1609587929392839161UL, P4 = 9650029242287828579UL, P5 = 2870177450012600261UL;
            Func<ulong, ulong, ulong> round = (acc, lane) => Rotate(acc + lane * P2, 31) * P1;
            int position = offset, end = offset + length;
            ulong hash;
            if (length >= 32)
            {
                ulong v1 = unchecked(P1 + P2), v2 = P2, v3 = 0, v4 = unchecked(0 - P1);
                while (end - position >= 32)
                {
                    v1 = round(v1, BitConverter.ToUInt64(data, position));
                    v2 = round(v2, BitConverter.ToUInt64(data, position + 8));
                    v3 = round(v3, BitConverter.ToUInt64(data, position + 16));
                    v4 = round(v4, BitConverter.ToUInt64(data, position + 24));
                    position += 32;
                }
                hash = Rotate(v1, 1) + Rotate(v2, 7) + Rotate(v3, 12) + Rotate(v4, 18);
                foreach (ulong v in new[] { v1, v2, v3, v4 }) hash = (hash ^ round(0, v)) * P1 + P4;
            }
            else hash = P5;
            hash += (ulong)length;
            for (; end - position >= 8; position += 8) hash = Rotate(hash ^ round(0, BitConverter.ToUInt64(data, position)), 27) * P1 + P4;
            if (end - position >= 4) { hash = Rotate(hash ^ BitConverter.ToUInt32(data, position) * P1, 23) * P2 + P3; position += 4; }
            for (; position < end; position++) hash = Rotate(hash ^ data[position] * P5, 11) * P1;
            hash ^= hash >> 33; hash *= P2; hash ^= hash >> 29; hash *= P3; hash ^= hash >> 32;
            return hash;
        }
        private static ulong Rotate(ulong value, int bits) { return value << bits | value >> (64 - bits); }
    }
}
