using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;
using static System.Console;

namespace YetAnotherStupidBenchmark
{
    public static class Benchmark
    {
        public static readonly int MinBufferSize = 256 * 1024; // 256 MB
        public static readonly string ExePath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location!)!;
        public static readonly string TestFilePath = Path.GetFullPath(Path.Join(ExePath, "../../../../Test.dat"));
        public static readonly string WarmupFilePath = Path.Join(Path.GetDirectoryName(TestFilePath), "Warmup.dat");


        public static void Main(string[] args)
        {
//            Test();
//            return;
            if (string.Join(" ", args).ToLowerInvariant() == "-g") {
                GenerateTestFile(WarmupFilePath, 100_000);
                GenerateTestFile(TestFilePath, 300_000_000);
                return;
            }
            Run(WarmupFilePath, true);
            Run(TestFilePath);
        }

        private static void GenerateTestFile(string filePath, long valueCount, int maxValue = 1_000_000)
        {
            const int PacketSize = 1_000;
            var rnd = new Random(15);
            long Min(long a, long b) => a < b ? a : b;

            WriteLine($"File: {filePath}");

            using var fs = new FileStream(filePath, FileMode.OpenOrCreate);
            fs.SetLength(0);
            var (l, sum) = (0L, 0L);
            while (l < valueCount) {
                var packetSize = (int) Min(PacketSize, valueCount - l);
                var values = Enumerable
                    .Range(0, packetSize)
                    .Select(_ => rnd.Next(maxValue))
                    .ToArray();
                sum += values.Sum();
                var buffer = values.Encode().ToArray();
                fs.Write(buffer);
                l += packetSize;
            }

            WriteLine($"  Size:        {fs.Position / 1024.0 / 1024:f3} MB");
            WriteLine($"  Value count: {l}");
            WriteLine($"  Sum:         {sum}");
        }

        public static void Test()
        {
            const int maxValue = (1 << (7 * 4)) - 1;
            var rnd = new Random(1);
            var buffer = Enumerable.Range(0, 4000).Select(_ => rnd.Next(maxValue)).Encode().ToArray().AsMemory();
            var r1 = ComputeSum(buffer, 0, 0);
            var r2 = ComputeSumSimd(buffer, 0, 0);
            WriteLine($"r1 = {r1}");
            WriteLine($"r2 = {r2}");
            if (r1 != r2)
                throw new ApplicationException("SIMD version doesn't work properly.");
        }

        public static IEnumerable<byte> Encode(this IEnumerable<int> source)
        {
            const uint maxValue = (1 << (7 * 4)) - 1;
            foreach (var n in source) {
                if (maxValue < (uint) n)
                    throw new ArgumentException($"One of values in source sequence is too large ({(uint) n} > {maxValue}).");
                var writing = false;
                for (var i = 21; i >= 0; i -= 7) {
                    var last = i == 0;
                    var o = (byte) (n >> i & 0x7f | (last ? 128 : 0));
                    writing |= o != 0 || last;
                    if (writing)
                        yield return o;
                }
            }
        }

        public static void Run(string fileName, bool warmup = false)
        {
            void Print(string message) {
                if (!warmup) WriteLine(message);
            }
            Print($"File: {fileName} ({new FileInfo(fileName).Length / 1024.0 / 1024:f3} MB)");

            var rb = Measure(() => ComputeBaseline(fileName));
            Print($"  Simple Sum (baseline):            {rb.Time.TotalMilliseconds:f3} ms");

            if (Avx2.IsSupported) {
                var rsm = Measure(() => ComputeSumMMF(fileName, ComputeSumSimd));
                Print($"  Unsafe SIMD Loop Sum (mmap):      {rsm.Time.TotalMilliseconds:f3} ms -> {rsm.Result}");
                var rsa = Measure(() => ComputeSumAsync(fileName, ComputeSumSimd).Result);
                Print($"  Unsafe SIMD Loop Sum (async):     {rsa.Time.TotalMilliseconds:f3} ms -> {rsa.Result}");
                var rs = Measure(() => ComputeSum(fileName, ComputeSumSimd));
                Print($"  Unsafe SIMD Loop Sum:             {rs.Time.TotalMilliseconds:f3} ms -> {rs.Result}");
            }

            var r1 = Measure(() => ComputeSum(fileName, ComputeSumUnsafeUnrolled));
            Print($"  Unsafe Unrolled Loop Sum:         {r1.Time.TotalMilliseconds:f3} ms -> {r1.Result}");
            var r1a = Measure(() => ComputeSumAsync(fileName, ComputeSumUnsafeUnrolled).Result);
            Print($"  Unsafe Unrolled Loop Sum (async): {r1a.Time.TotalMilliseconds:f3} ms -> {r1a.Result}");

            var r2 = Measure(() => ComputeSum(fileName, ComputeSumUnrolled));
            Print($"  Unrolled Loop Sum:                {r2.Time.TotalMilliseconds:f3} ms -> {r2.Result}");

//            var r3 = Measure(() => ComputeSum(fileName, ComputeSumChecked));
//            Print($"  Checked Sum:                      {r3.Time.TotalMilliseconds:f3} ms -> {r3.Result}");

            var r = Measure(() => OriginalComputeSum(fileName));
            Print($"  Original Sum:                     {r.Time.TotalMilliseconds:f3} ms -> {r.Result}");
        }

        public static (T Result, TimeSpan Time) Measure<T>(Func<T> func)
        {
            var sw = new Stopwatch();
            sw.Start();
            var result = func();
            sw.Stop();
            return (result, sw.Elapsed);
        }

        public static long ComputeBaseline(string fileName)
        {
            using var fs = new FileStream(fileName, FileMode.Open);
            using var lease = MemoryPool<byte>.Shared.Rent(MinBufferSize);
            var buffer = lease.Memory;

            var sum = 0L;
            while (true) {
                var count = fs.Read(buffer.Span);
                if (count == 0)
                    return sum;
                sum += BaselineSum(buffer.Span.Slice(0, count));
            }
        }

        public static long ComputeSum(string fileName, Func<ReadOnlyMemory<byte>, long, int, (long, int)> sumComputer)
        {
            using var fs = new FileStream(fileName, FileMode.Open);
            using var lease = MemoryPool<byte>.Shared.Rent(MinBufferSize);
            var buffer = lease.Memory;

            long sum = 0;
            int n = 0;
            while (true) {
                var count = fs.Read(buffer.Span);
                if (count == 0)
                    return sum + n;
                (sum, n) = sumComputer(buffer.Slice(0, count), sum, n);
            }
        }

        public static long ComputeSumMMF(string fileName, Func<IntPtr, long, long, int, (long, int)> sumComputer)
        {
            using var f = MemoryMappedFile.CreateFromFile(fileName, FileMode.Open);
            using var fAccessor = f.CreateViewAccessor();
            var fHandle = fAccessor.SafeMemoryMappedViewHandle;
            var (sum, _) = sumComputer(fHandle.DangerousGetHandle(), (long) fHandle.ByteLength, 0, 0);
            return sum;
        }

        public static async Task<long> ComputeSumAsync(string fileName, Func<ReadOnlyMemory<byte>, long, int, (long, int)> sumComputer, CancellationToken ct = default)
        {
            await using var fs = new FileStream(fileName, FileMode.Open);
            var pipe = new Pipe(new PipeOptions(minimumSegmentSize: MinBufferSize, useSynchronizationContext: false));

            async Task ProduceAsync()
            {
                await fs.CopyToAsync(pipe.Writer, ct);
                pipe.Writer.Complete();
            }

            async Task<long> ConsumeAsync() {
                try {
                    var sum = 0L;
                    var n = 0;
                    var lastTimeProcessed = 0L;
                    var readTask = pipe.Reader.ReadAsync(ct);
                    while (true) {
                        var result = await readTask.ConfigureAwait(false);
                        var buffer = result.Buffer;
                        var toProcess = buffer.Slice(lastTimeProcessed);
                        lastTimeProcessed = toProcess.Length;
                        if (toProcess.IsEmpty && (result.IsCompleted || result.IsCanceled))
                            break;
                        pipe.Reader.AdvanceTo(toProcess.Start, toProcess.End);
                        readTask = pipe.Reader.ReadAsync(ct); // We can start it right now to read while compute
                        foreach (var segment in toProcess)
                            (sum, n) = sumComputer(segment.Slice(0), sum, n);
                    }
                    return sum + n;
                }
                finally {
                    pipe.Reader.Complete();
                }
            }

            var (produceTask, consumeTask) = (ProduceAsync(), ConsumeAsync());
            await Task.WhenAll(produceTask, consumeTask);
            return consumeTask.Result;
        }

        public static long OriginalComputeSum(string fileName)
        {
            using var fs = new FileStream(fileName, FileMode.Open);
            long sum = 0;
            int n = 0;
            while (true) {
                var b = fs.ReadByte();
                while (b < 128) {
                    if (b == -1)
                        return sum + n;
                    n = (n << 7) + b;
                    b = fs.ReadByte();
                }
                sum += (n << 7) + b - 128;
                n = 0;
            }
        }

        private static long BaselineSum(Span<byte> buffer)
        {
            var sum = 0L;
            foreach (var value in MemoryMarshal.Cast<byte, long>(buffer))
                sum += value;
            return sum;
        }

        private static (long, int) ComputeSumChecked(ReadOnlyMemory<byte> buffer, long sum, int n)
        {
            var span = buffer.Span;
            var sequenceLength = 0;
            foreach (var b in span) {
                if (b < 128) {
                    if (++sequenceLength >= 4)
                        // Not perfect -- i.e. doesn't detect 5+ byte sequences on buffer boundaries,
                        // but prob. enough to detect an issue w/ encoding in most cases
                        throw new ArgumentException("Invalid data in buffer.");
                    n = (n << 7) + b;
                }
                else {
                    sum += (n << 7) + b - 128;
                    n = 0;
                    sequenceLength = 0;
                }
            }
            return (sum, n);
        }

        private static (long, int) ComputeSum(ReadOnlyMemory<byte> buffer, long sum, int n)
        {
            var span = buffer.Span;
            foreach (var b in span) {
                if (b < 128)
                    n = (n << 7) + b;
                else {
                    sum += (n << 7) + b - 128;
                    n = 0;
                }
            }
            return (sum, n);
        }

        private static unsafe (long, int) ComputeSumUnsafe(ReadOnlyMemory<byte> buffer, long sum, int n) {
            var span = buffer.Span;
            fixed (byte* pStart = span) {
                var pEnd = pStart + span.Length;
                var p = pStart;
                while (p < pEnd) {
                    var b = *p++;
                    if (b < 128)
                        n = (n << 7) + b;
                    else {
                        sum += (n << 7) + b - 128;
                        n = 0;
                    }
                }
            }
            return (sum, n);
        }

        private static unsafe (long, int) ComputeSumUnsafeUnrolledNoBranching(ReadOnlyMemory<byte> buffer, long sum, int n)
        {
            const ulong M80 = 0x8080808080808080ul;
            const ulong M7F = 0x7F7F7F7F7F7F7F7Ful;

            var span = buffer.Span;
            fixed (byte* pStart = span) {
                var pEnd = pStart + span.Length;
                var pEnd8 = pEnd - 7;
                var p = pStart;
                while (p < pEnd8) {
                    var b = *(ulong*)p;
                    var bFlags = (b & M80) >> 7;
                    var bValues = b & M7F;
                    // Loop
                    var done = 0 - (int) (bFlags & 1);
                    n = (n << 7) + (int) (bValues & 0xFF);
                    sum += n & done;
                    n &= ~done;
                    // Loop
                    done = 0 - (int) (bFlags >> (1*8) & 1);
                    n = (n << 7) + (int) (bValues >> (1*8) & 0xFF);
                    sum += n & done;
                    n &= ~done;
                    // Loop
                    done = 0 - (int) (bFlags >> (2*8) & 1);
                    n = (n << 7) + (int) (bValues >> (2*8) & 0xFF);
                    sum += n & done;
                    n &= ~done;
                    // Loop
                    done = 0 - (int) (bFlags >> (3*8) & 1);
                    n = (n << 7) + (int) (bValues >> (3*8) & 0xFF);
                    sum += n & done;
                    n &= ~done;
                    // Loop
                    done = 0 - (int) (bFlags >> (4*8) & 1);
                    n = (n << 7) + (int) (bValues >> (4*8) & 0xFF);
                    sum += n & done;
                    n &= ~done;
                    // Loop
                    done = 0 - (int) (bFlags >> (5*8) & 1);
                    n = (n << 7) + (int) (bValues >> (5*8) & 0xFF);
                    sum += n & done;
                    n &= ~done;
                    // Loop
                    done = 0 - (int) (bFlags >> (6*8) & 1);
                    n = (n << 7) + (int) (bValues >> (6*8) & 0xFF);
                    sum += n & done;
                    n &= ~done;
                    // Loop
                    done = 0 - (int) (bFlags >> (7*8) & 1);
                    n = (n << 7) + (int) (bValues >> (7*8) & 0xFF);
                    sum += n & done;
                    n &= ~done;

                    p += 8;
                }
                while (p < pEnd) {
                    var b = *p++;
                    if (b < 128)
                        n = (n << 7) + b;
                    else {
                        sum += (n << 7) + b - 128;
                        n = 0;
                    }
                }
            }
            return (sum, n);
        }

        private static unsafe (long, int) ComputeSumUnsafeUnrolled(ReadOnlyMemory<byte> buffer, long sum, int n)
        {
            var span = buffer.Span;
            fixed (byte* pStart = span) {
                var pEnd = pStart + span.Length;
                var pEnd16 = pEnd - 15;
                var p = pStart;
                while (p < pEnd16) {
                    // Loop
                    var b = p[0];
                    n = (b & 127) + (n << 7);
                    if ((b & 128) != 0) {
                        sum += n;
                        n = 0;
                    }
                    // Loop
                    b= p[1];
                    n = (b & 127) + (n << 7);
                    if ((b & 128) != 0) {
                        sum += n;
                        n = 0;
                    }
                    // Loop
                    b= p[2];
                    n = (b & 127) + (n << 7);
                    if ((b & 128) != 0) {
                        sum += n;
                        n = 0;
                    }
                    // Loop
                    b= p[3];
                    n = (b & 127) + (n << 7);
                    if ((b & 128) != 0) {
                        sum += n;
                        n = 0;
                    }
                    // Loop
                    b= p[4];
                    n = (b & 127) + (n << 7);
                    if ((b & 128) != 0) {
                        sum += n;
                        n = 0;
                    }
                    // Loop
                    b= p[5];
                    n = (b & 127) + (n << 7);
                    if ((b & 128) != 0) {
                        sum += n;
                        n = 0;
                    }
                    // Loop
                    b= p[6];
                    n = (b & 127) + (n << 7);
                    if ((b & 128) != 0) {
                        sum += n;
                        n = 0;
                    }
                    // Loop
                    b= p[7];
                    n = (b & 127) + (n << 7);
                    if ((b & 128) != 0) {
                        sum += n;
                        n = 0;
                    }
                    // Loop
                    b= p[8];
                    n = (b & 127) + (n << 7);
                    if ((b & 128) != 0) {
                        sum += n;
                        n = 0;
                    }
                    // Loop
                    b= p[9];
                    n = (b & 127) + (n << 7);
                    if ((b & 128) != 0) {
                        sum += n;
                        n = 0;
                    }
                    // Loop
                    b= p[10];
                    n = (b & 127) + (n << 7);
                    if ((b & 128) != 0) {
                        sum += n;
                        n = 0;
                    }
                    // Loop
                    b= p[11];
                    n = (b & 127) + (n << 7);
                    if ((b & 128) != 0) {
                        sum += n;
                        n = 0;
                    }
                    // Loop
                    b= p[12];
                    n = (b & 127) + (n << 7);
                    if ((b & 128) != 0) {
                        sum += n;
                        n = 0;
                    }
                    // Loop
                    b= p[13];
                    n = (b & 127) + (n << 7);
                    if ((b & 128) != 0) {
                        sum += n;
                        n = 0;
                    }
                    // Loop
                    b= p[14];
                    n = (b & 127) + (n << 7);
                    if ((b & 128) != 0) {
                        sum += n;
                        n = 0;
                    }
                    // Loop
                    b= p[15];
                    n = (b & 127) + (n << 7);
                    if ((b & 128) != 0) {
                        sum += n;
                        n = 0;
                    }

                    p += 16;
                }
                while (p < pEnd) {
                    var b = *p++;
                    n = (b & 127) + (n << 7);
                    if ((b & 128) != 0) {
                        sum += n;
                        n = 0;
                    }
                }
            }
            return (sum, n);
        }

        private static (long, int) ComputeSumUnrolled(ReadOnlyMemory<byte> buffer, long sum, int n)
        {
            var span = buffer.Span;
            var span8 = MemoryMarshal.Cast<byte, ulong>(span);
            foreach (var v in span8) {
                // Loop
                var b = (int) (v & 255);
                n = (b & 127) + (n << 7);
                if ((b & 128) != 0) {
                    sum += n;
                    n = 0;
                }
                // Loop
                b = (int) (v >> 8 & 255);
                n = (b & 127) + (n << 7);
                if ((b & 128) != 0) {
                    sum += n;
                    n = 0;
                }
                // Loop
                b = (int) (v >> 16 & 255);
                n = (b & 127) + (n << 7);
                if ((b & 128) != 0) {
                    sum += n;
                    n = 0;
                }
                // Loop
                b = (int) (v >> 24 & 255);
                n = (b & 127) + (n << 7);
                if ((b & 128) != 0) {
                    sum += n;
                    n = 0;
                }
                // Loop
                b = (int) (v >> 32 & 255);
                n = (b & 127) + (n << 7);
                if ((b & 128) != 0) {
                    sum += n;
                    n = 0;
                }
                // Loop
                b = (int) (v >> 40 & 255);
                n = (b & 127) + (n << 7);
                if ((b & 128) != 0) {
                    sum += n;
                    n = 0;
                }
                // Loop
                b = (int) (v >> 48 & 255);
                n = (b & 127) + (n << 7);
                if ((b & 128) != 0) {
                    sum += n;
                    n = 0;
                }
                // Loop
                b = (int) (v >> 56 & 255);
                n = (b & 127) + (n << 7);
                if ((b & 128) != 0) {
                    sum += n;
                    n = 0;
                }
            }
            var span1 = span.Slice(span8.Length*8);
            foreach (var b in span1) {
                n = (b & 127) + (n << 7);
                if ((b & 128) != 0) {
                    sum += n;
                    n = 0;
                }
            }
            return (sum, n);
        }

        private static unsafe (long, int) ComputeSumSimd(ReadOnlyMemory<byte> buffer, long sum, int n)
        {
            var span = buffer.Span;
            fixed (byte* pStart = span) {
                return ComputeSumSimd(new IntPtr(pStart), span.Length, sum, n);
            }
        }

        private static unsafe (long, int) ComputeSumSimd(IntPtr start, long length, long sum, int n)
        {
            var b7F = (byte) 127;
            var bFF = (byte) 255;
            var m7F = Avx2.BroadcastScalarToVector256(&b7F);
            var mFF = Avx2.BroadcastScalarToVector256(&bFF);
            var sum0 = Vector256<long>.Zero;
            var sum7 = Vector256<long>.Zero;
            var sum14 = Vector256<long>.Zero;
            var sum21 = Vector256<long>.Zero;

            var p = (byte*) start;
            var pEnd = p + length;

            // The following SIMD loop assumes n == 0 when it starts,
            // so we have to reach this point "manually" here
            if (n != 0) {
                while (p < pEnd) {
                    var b = *p++;
                    n = (b & 127) + (n << 7);
                    if ((b & 128) != 0) {
                        sum += n;
                        n = 0;
                        break;
                    }
                }
            }

            // SIMD loop
            while (p + 36 <= pEnd) {
                // Offset 0
                var x = Avx2.LoadVector256(p);
                // f indicates whether *p has flag (assuming p iterates through vector indexes);
                // f[i] is either 0 (no flag) or -1 (i.e. all byte bits are set)
                var f = Avx2.CompareGreaterThan(Vector256<sbyte>.Zero, x.AsSByte()).AsByte();
                x = Avx2.And(x, m7F);

                // Offset 1
                var x1 = Avx2.LoadVector256(p + 1);
                var f1 = Avx2.CompareGreaterThan(Vector256<sbyte>.Zero, x1.AsSByte()).AsByte();
                // f01 indicates whether *p flag sequence is (0,1); similarly, f[i] is either 0 or -1
                var f01 = Avx2.CompareGreaterThan(f.AsSByte(), f1.AsSByte()).AsByte();

                // Offset 2
                var x2 = Avx2.LoadVector256(p + 2);
                var f2 = Avx2.CompareGreaterThan(Vector256<sbyte>.Zero, x2.AsSByte()).AsByte();
                var f00 = Avx2.Or(f, f1);
                // f001 indicates whether *p flag sequence is (0,0,1)
                var f001 = Avx2.CompareGreaterThan(f00.AsSByte(), f2.AsSByte()).AsByte();

                var f000 = Avx2.Or(f00, f2);
                // f0001 indicates whether *p flag sequence is (0,0,0,1)
                // we assume here that the 4th byte always has a flag (i.e. the encoding
                // is valid), so we don't read it.
                var f0001 = Avx2.CompareGreaterThan(f000.AsSByte(), mFF.AsSByte()).AsByte();

                sum0 = Avx2.Add(sum0, Avx2.SumAbsoluteDifferences(Avx2.And(x, f), Vector256<byte>.Zero).AsInt64());
                sum7 = Avx2.Add(sum7, Avx2.SumAbsoluteDifferences(Avx2.And(x, f01), Vector256<byte>.Zero).AsInt64());
                sum14 = Avx2.Add(sum14, Avx2.SumAbsoluteDifferences(Avx2.And(x, f001), Vector256<byte>.Zero).AsInt64());
                sum21 = Avx2.Add(sum21, Avx2.SumAbsoluteDifferences(Avx2.And(x, f0001), Vector256<byte>.Zero).AsInt64());

                p += 32;
            }

            var s07 = Avx2.Add(sum0, Avx2.ShiftLeftLogical(sum7, 7));
            var s1421 = Avx2.Add(Avx2.ShiftLeftLogical(sum14, 14), Avx2.ShiftLeftLogical(sum21, 21));
            var s = Avx2.Add(s07, s1421);

            sum += s.GetElement(0) + s.GetElement(1) + s.GetElement(2) + s.GetElement(3);
            n = 0; // Fine assuming we'll process 4+ items in the following loop

            while (p < pEnd) {
                var b = *p++;
                n = (b & 127) + (n << 7);
                if ((b & 128) != 0) {
                    sum += n;
                    n = 0;
                }
            }
            return (sum, n);
        }
    }
}
