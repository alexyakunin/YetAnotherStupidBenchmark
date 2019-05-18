using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using static System.Console;

namespace YetAnotherStupidBenchmark
{
    class Benchmark
    {
        public static int MinBufferSize = 1 << 16;

        public static void Main()
        {
            if (Environment.OSVersion.Platform == PlatformID.Unix) {
//                Run("/home/alex/.aliases");
                Run("/home/alex/Downloads/dotnet-sdk-3.0.100-preview5-011568-linux-x64.tar.gz");
            }
            else {
                Run("C:\\Downloads\\dotnet-sdk-3.0.100-preview5-011568-win-x64.exe "); // ~ 130 MB -- warmup
                Run("C:\\Downloads\\26_dump.zip"); // ~ 1.7 GB
            }
        }
        
        public static void Run(string fileName)
        {
            WriteLine($"File: {fileName} ({new FileInfo(fileName).Length / 1024.0 / 1024:f3} MB)");
            var r1 = Measure(() => ComputeSum(fileName));
            WriteLine($"  ComputeSum:      {r1.Result} in {r1.Time.TotalMilliseconds:f3} ms");
            var r2 = Measure(() => Task.Run(() => ComputeSumAsync(fileName)).Result);
            WriteLine($"  ComputeSumAsync: {r2.Result} in {r2.Time.TotalMilliseconds:f3} ms");
            var r3 = Measure(() => OldComputeSum(fileName));
            WriteLine($"  OldComputeSum:   {r3.Result} in {r3.Time.TotalMilliseconds:f3} ms");
            WriteLine($"  Are equal?       {r1.Result == r2.Result && r2.Result == r3.Result}");
        }

        public static (T Result, TimeSpan Time) Measure<T>(Func<T> func)
        {
            var sw = new Stopwatch();
            sw.Start();
            var result = func();
            sw.Stop();
            return (result, sw.Elapsed); 
        }

        public static async Task<long> ComputeSumAsync(string fileName, CancellationToken ct = default)
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
                            ProcessSpanUnsafe(segment.Span, ref sum, ref n);
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

        public static long ComputeSum(string fileName)
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
                ProcessSpanUnsafe(buffer.Span.Slice(0, count), ref sum, ref n);
            }
        }

        public static long OldComputeSum(string fileName)
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

        private static void ProcessSpan(ReadOnlySpan<byte> span, ref long sum, ref int n) {
            var (sum1, n1) = (sum, n); // Copying to locals -- I suspect JIT compiler won't do this
            foreach (var b in span) {
                if (b < 128)
                    n1 = (n1 << 7) + b;
                else {
                    sum1 += (n1 << 7) + b - 128;
                    n1 = 0;
                }
            }
            (sum, n) = (sum1, n1);
        }

        private static unsafe void ProcessSpanUnsafe(ReadOnlySpan<byte> span, ref long sum, ref int n) {
            var (sum1, n1) = (sum, n); // Copying to locals -- I suspect JIT compiler won't do this
            fixed (byte* pStart = span) {
                var pEnd = pStart + span.Length;
                var p = pStart;
                while (p < pEnd) {
                    var b = *p++;
                    if (b < 128)
                        n1 = (n1 << 7) + b;
                    else {
                        sum1 += (n1 << 7) + b - 128;
                        n1 = 0;
                    }
                }
            }
            (sum, n) = (sum1, n1);
        }
    }
}
