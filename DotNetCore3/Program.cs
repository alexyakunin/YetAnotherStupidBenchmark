using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static System.Console;

namespace YetAnotherStupidBenchmark
{
    class Benchmark
    {
        public static int MinBufferSize = 1 << 17;

        public static void Main()
        {
            if (Environment.OSVersion.Platform == PlatformID.Unix) {
                Run("/home/alex/.profile", true);
                Run("/home/alex/Projects/YASB_Dan/rle.dat");
            }
            else {
                Run("C:\\Downloads\\dotnet-sdk-3.0.100-preview5-011568-win-x64.exe ", true); // ~ 130 MB -- warmup
                Run("C:\\Downloads\\26_dump.zip"); // ~ 1.7 GB
            }
        }
        
        public static void Run(string fileName, bool warmup = false)
        {
            void Print(string message) {
                if (!warmup) WriteLine(message);
            }
            Print($"File: {fileName} ({new FileInfo(fileName).Length / 1024.0 / 1024:f3} MB)");
            
            var r0 = Measure(() => ComputeSimpleSum(fileName));
            Print($"  Simple Sum (baseline):            {r0.Time.TotalMilliseconds:f3} ms");
            
            var r1 = Measure(() => ComputeSum(fileName, ComputeSumUnsafeUnrolled));
            Print($"  Unsafe Unrolled Loop Sum:         {r1.Time.TotalMilliseconds:f3} ms -> {r1.Result}");
            var r1a = Measure(() => ComputeSumAsync(fileName, ComputeSumUnsafeUnrolled).Result);
            Print($"  Unsafe Unrolled Loop Sum (async): {r1a.Time.TotalMilliseconds:f3} ms -> {r1a.Result}");

            var r2 = Measure(() => ComputeSum(fileName, ComputeSumUnrolled));
            Print($"  Unrolled Loop Sum:                {r2.Time.TotalMilliseconds:f3} ms -> {r2.Result}");
            
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

        public static long ComputeSimpleSum(string fileName)
        {
            using var fs = new FileStream(fileName, FileMode.Open);
            using var lease = MemoryPool<byte>.Shared.Rent(MinBufferSize);
            var buffer = lease.Memory;

            var sum = 0L;
            while (true) {
                var count = fs.Read(buffer.Span);
                if (count == 0)
                    return sum;
                sum += SimpleSum(buffer.Span.Slice(0, count));
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

        public static async Task<long> ComputeSumAsync(string fileName, Func<ReadOnlyMemory<byte>, long, int, (long, int)> sumComputer, CancellationToken ct = default)
        {
            await using var fs = new FileStream(fileName, FileMode.Open);
            var pipe = new Pipe();

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

        private static long SimpleSum(Span<byte> buffer)
        {
            var sum = 0L;
            foreach (var value in MemoryMarshal.Cast<byte, long>(buffer))
                sum += value;
            return sum;
        }

        private static unsafe long SimpleSum2(Span<byte> buffer)
        {
            var sum = 0L;
            fixed (byte* pStart = buffer) {
                var pEnd = pStart + buffer.Length - 7;
                var p = (long*) pStart;
                while (p < pEnd) 
                    sum += *p++;
            }
            return sum;
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
    }
}
