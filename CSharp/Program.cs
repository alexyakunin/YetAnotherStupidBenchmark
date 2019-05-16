using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using static System.Console;

namespace YetAnotherStupidBenchmark
{
    class RleDecodeTest
    {
        public static int MinBufferSize = 1 << 16;

        public static void Main()
        {
            Run("C:\\Downloads\\node-v9.9.0-x64.msi"); // 16 MB -- warmup
            Run("C:\\Downloads\\26_dump.zip"); // ~ 1.7 GB
        }
        
        public static void Run(string fileName)
        {
            WriteLine($"File: {fileName} ({new FileInfo(fileName).Length / 1024.0 / 1024:f3} MB)");
            var r1 = Measure(() => Task.Run(() => ComputeSumAsync(fileName)).Result);
            WriteLine($"  ComputeSumAsync: {r1.Result} in {r1.Time.TotalMilliseconds:f3} ms");
            var r2 = Measure(() => ComputeSum(fileName));
            WriteLine($"  ComputeSum:      {r2.Result} in {r2.Time.TotalMilliseconds:f3} ms");
            WriteLine($"  Are equal?       {r1.Result == r2.Result}");
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

            async Task ProduceAsync() {
                while (true) {
                    var buffer = pipe.Writer.GetMemory(MinBufferSize);
                    var byteCount = await fs.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (byteCount == 0) 
                        break;
                    pipe.Writer.Advance(byteCount);
                    if ((await pipe.Writer.FlushAsync(ct).ConfigureAwait(false)).IsCompleted)
                        break;
                }
                pipe.Writer.Complete();
            }

            // Can't embed this into ConsumeAsync b/c async methods can't have Span<T> locals
            void ProcessSpan(ReadOnlySpan<byte> span, ref long sum, ref int n) {
                var (sum1, n1) = (sum, n);
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

            async Task<long> ConsumeAsync() {
                var sum = 0L;
                var n = 0; 
                while (true) {
                    var result = await pipe.Reader.ReadAsync(ct).ConfigureAwait(false);
                    var buffer = result.Buffer;
                    if (buffer.IsEmpty && result.IsCompleted)
                        break;
                    foreach (var segment in buffer)
                        ProcessSpan(segment.Span, ref sum, ref n);
                    pipe.Reader.AdvanceTo(buffer.End);
                }
                pipe.Reader.Complete();
                return sum + n;
            }

            var (produceTask, consumeTask) = (ProduceAsync(), ConsumeAsync());
            await Task.WhenAll(produceTask, consumeTask);
            return consumeTask.Result;
        }

        public static long ComputeSum(string fileName)
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
    }
}
