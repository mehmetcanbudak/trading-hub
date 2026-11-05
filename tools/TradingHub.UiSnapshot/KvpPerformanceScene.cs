using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Headless;
using SkiaSharp;
using TradingHub.App;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Tests;

internal static class KvpPerformanceScene
{
    private sealed record Sample(double Milliseconds, long ManagedBytes);
    private sealed record Timing(string Scope, int Warmups, IReadOnlyList<Sample> Samples, double MedianMilliseconds, long MedianManagedBytes);

    private static Timing Measure(string scope, Action operation, int warmups = 3, int repetitions = 12)
    {
        for (int i = 0; i < warmups; i++) operation();
        var samples = new Sample[repetitions];
        for (int i = 0; i < repetitions; i++)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            operation();
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            samples[i] = new(elapsed, GC.GetAllocatedBytesForCurrentThread() - allocated);
        }
        return new(scope, warmups, samples, samples.Select(value => value.Milliseconds).Order().ElementAt(repetitions / 2),
            samples.Select(value => value.ManagedBytes).Order().ElementAt(repetitions / 2));
    }

    public static int Run(MainWindow owner, Action<double> pump, string outDir)
    {
        outDir = Path.GetFullPath(outDir);
        int checks = 0, failures = 0;
        var evidence = new List<object>();
        var timings = new List<Timing>();
        var depthMeasurements = new List<object>();
        void Check(bool condition, string description)
        {
            checks++; if (!condition) failures++;
            evidence.Add(new { condition, description });
            Console.WriteLine($"{(condition ? "OK" : "FAIL")} KVP performance: {description}");
        }
        T Await<T>(Task<T> task, string description)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted && DateTime.UtcNow < until) pump(0.02);
            Check(task.IsCompleted, description);
            return task.GetAwaiter().GetResult();
        }
        string runtimeRoot = Path.Combine(AppPaths.Root, "performance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeRoot);
        double oldHours = App.Services.Settings.RawHistoryHours;
        App.Services.Settings.RawHistoryHours = 2160;
        App.Services.ApplyOperationsHistorySettings();
        var now = new DateTime(2026, 11, 4, 16, 0, 0, DateTimeKind.Utc);
        try
        {
            var instrument = new Instrument { FeedId = "cpu-profile-" + Guid.NewGuid().ToString("N"), Symbol = "SIGNED-SPARSE-PROFILE", TickSize = 0.25m, VolumeStep = 1, PointValue = 2 };
            const int bars = 12_000, printsPerBar = 8;
            var trades = new Trade[bars * printsPerBar];
            decimal expectedVolume = 0;
            for (int i = 0; i < trades.Length; i++)
            {
                decimal quantity = 1 + i % 5;
                decimal price = -250 + (i * 37 % 2000) * 0.25m;
                var side = i % 3 == 0 ? TradeSide.Buy : i % 3 == 1 ? TradeSide.Sell : TradeSide.None;
                trades[i] = new Trade(i + 1, now.AddMinutes(i / printsPerBar - bars).AddSeconds(i % printsPerBar * 7), price, quantity, side);
                expectedVolume += quantity;
            }
            using (var fixture = KvpDepthHistoryFixture.Open(owner, pump, Path.Combine(runtimeRoot, "profile"), instrument, trades, now, ChartPeriod.M1))
            using (var bitmap = new SKBitmap(1280, 800))
            using (var canvas = new SKCanvas(bitmap))
            {
                var model = fixture.Model;
                Check(model.Series.Count == bars, "bounded initial aggregation reconstructs the representative 96,000-print / 12,000-bar workload");
                var drawing = new ChartDrawing { Kind = DrawingKind.FixedRangeProfile, ProfileRows = 256,
                    Points = [new(trades[0].Time, (double)trades[0].Price), new(now, (double)trades[^1].Price)] };
                var vwap = new ChartDrawing { Kind = DrawingKind.AnchoredVwap, Points = [new(trades[0].Time, (double)trades[0].Price)] };
                model.Drawings.Add(drawing);
                model.Drawings.Add(vwap);
                var calculator = new TradeDrawingCalc();
                TradeDrawingCalc.ProfileResult? reference = new TradeDrawingCalc().FixedRangeProfile(model.Data.Trades, model.Series, trades[0].Time, now, 256);
                TradeDrawingCalc.ProfileResult? selected = calculator.FixedRangeProfile(model.Data.Trades, model.Series, trades[0].Time, now, 256);
                Check(reference != null && selected != null && reference.Total == expectedVolume && selected.Total == expectedVolume &&
                    reference.Rows.SequenceEqual(selected.Rows) && reference.Poc == selected.Poc && reference.Vah == selected.Vah && reference.Val == selected.Val,
                    "uncached CPU reference and selected bounded cache conserve independent decimal totals and identical row/POC/value-area values");
                timings.Add(Measure("CPU reference: full uncached production profile recomputation", () =>
                {
                    var value = new TradeDrawingCalc().FixedRangeProfile(model.Data.Trades, model.Series, trades[0].Time, now, 256);
                    if (value?.Total != expectedVolume) throw new InvalidDataException("CPU reference changed conserved volume.");
                }));
                timings.Add(Measure("Selected production profile: warm unchanged-data cache", () =>
                {
                    var value = calculator.FixedRangeProfile(model.Data.Trades, model.Series, trades[0].Time, now, 256);
                    if (value?.Total != expectedVolume) throw new InvalidDataException("Selected profile changed conserved volume.");
                }));
                var renderer = new ChartRenderer();
                var state = new ChartRenderState();
                void Frame(bool cold)
                {
                    if (cold) state.ClearDrawingRuntime();
                    model.Update();
                    renderer.Render(canvas, model, bitmap.Width, bitmap.Height, state);
                    canvas.Flush();
                }
                Frame(true);
                byte[] referencePixels = bitmap.GetPixelSpan().ToArray();
                Frame(false);
                Check(referencePixels.AsSpan().SequenceEqual(bitmap.GetPixelSpan()), "actual cold-versus-selected Skia chart frames are byte-identical with both trade drawings");
                timings.Add(Measure("End-to-end actual Skia chart frame: cold profile/VWAP runtime", () => Frame(true)));
                timings.Add(Measure("End-to-end actual Skia chart frame: retained per-chart runtime", () => Frame(false)));
                using var image = SKImage.FromBitmap(bitmap);
                using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(outDir, "kvp_performance_actual_cpu_chart.png"), png.ToArray());
                Check(state.FindDrawingRuntime(drawing)?.CalculateProfile(drawing, model.Data.Trades, model.Series, model.DataGeneration)?.Total == expectedVolume,
                    "measured rendering used the actual selected per-chart drawing calculator");
            }

            // The real framed writer reports disk bytes, saved/dropped coverage and projections. A burst
            // is deliberately labelled as such rather than claimed as an entitled provider update rate.
            long writerAllocated = GC.GetTotalAllocatedBytes(precise: true);
            var sizing = Await(DepthRecordingSizingFixture.MeasureAsync(Path.Combine(runtimeRoot, "writer-sizing"),
                levelsPerSide: 1000, changesPerUpdate: 64, books: 200, requestedRate: 10, producerDelay: TimeSpan.Zero),
                "actual wide-book framed writer burst drains with observable retained bytes/drops");
            Check(sizing.SavedBooks > 0 && sizing.RetainedDiskBytes == sizing.BookFrameBytes + sizing.GapFrameBytes + sizing.HeaderBytes,
                "writer sizing reconciles actual on-disk framed/header bytes, not a guessed memory layout");
            depthMeasurements.Add(new { scope = "Actual writer-only burst; 10 Hz projections are not throughput certification",
                measurement = sizing, measuredProcessManagedBytes = GC.GetTotalAllocatedBytes(precise: true) - writerAllocated });
            writerAllocated = GC.GetTotalAllocatedBytes(precise: true);
            var paced = Await(DepthRecordingSizingFixture.MeasureAsync(Path.Combine(runtimeRoot, "writer-paced"),
                levelsPerSide: 1000, changesPerUpdate: 64, books: 40, requestedRate: 10, producerDelay: TimeSpan.FromMilliseconds(100)),
                "actual paced wide-book framed writer drains separately from burst measurement");
            Check(paced.SavedBooks > 0 && paced.RecordedBidLevels == 1000 && paced.RecordedAskLevels == 1000 && paced.RecordedChanges == 64,
                "paced writer records actual bounded levels and received changes rather than silently measuring an empty book");
            depthMeasurements.Add(new { scope = "Actual paced 100 ms producer plus writer drain; finite fake workload only",
                measurement = paced, measuredProcessManagedBytes = GC.GetTotalAllocatedBytes(precise: true) - writerAllocated });

            foreach (bool record in new[] { false, true })
            {
                string label = record ? "recorded" : "display-only";
                var bookInstrument = new Instrument { FeedId = "wide-book-" + label + "-" + Guid.NewGuid().ToString("N"), Symbol = "WIDE-SIGNED-BOOK", Exchange = "isolated-wide-book", TickSize = 0.25m, VolumeStep = 0.01m };
                var bookTrades = Enumerable.Range(0, 30).Select(i => new Trade(i + 1, now.AddMinutes(i - 30), -250 + i % 3 * .25m, 1, TradeSide.Buy)).ToArray();
                string bookRoot = Path.Combine(runtimeRoot, "book-" + label);
                using var fixture = KvpDepthHistoryFixture.Open(owner, pump, bookRoot, bookInstrument, bookTrades, now, ChartPeriod.M1,
                    new DepthRecordingOptions { Enabled = record });
                fixture.Model.Settings.ShowLiquidityHeatmap = true;
                using var bitmap = new SKBitmap(1280, 800);
                using var canvas = new SKCanvas(bitmap);
                var renderer = new ChartRenderer();
                var state = new ChartRenderState();
                // Warm real chart/font/heatmap code outside the measured phase; writer workload below
                // still measures the first owned recorded book and its required baseline.
                renderer.Render(canvas, fixture.Model, bitmap.Width, bitmap.Height, state);
                canvas.Flush();
                long allocated = GC.GetTotalAllocatedBytes(precise: true);
                long started = Stopwatch.GetTimestamp();
                const int books = 60;
                long peakInputQueueBytes = 0;
                for (int i = 0; i < books; i++)
                {
                    DateTime at = now.AddMilliseconds(i * 100);
                    var input = DepthRecordingSizingFixture.WideBook(at, 1000, 64, i + 1, 1.25m + i / 100m);
                    fixture.Push(new DepthUpdate
                    {
                        Snapshot = input.Snapshot, Changes = input.Changes, ProviderId = bookInstrument.FeedId,
                        Venue = bookInstrument.Exchange, ReceivedAt = at, Sequence = i + 1,
                        PreviousSequence = i == 0 ? null : i, IsFullRefresh = i == 0, Coverage = input.Coverage,
                    }, pump: false);
                    peakInputQueueBytes = Math.Max(peakInputQueueBytes, fixture.Data.QueuedDepthBytes);
                    fixture.Data.Pump();
                    fixture.Model.Update();
                    renderer.Render(canvas, fixture.Model, bitmap.Width, bitmap.Height, state);
                    canvas.Flush();
                    pump(0.1); // Real paced UI work: includes application frames and the owned asynchronous writer.
                }
                DateTime end = now.AddSeconds(6);
                fixture.StopWriter(end);
                fixture.Select(now, end);
                renderer.Render(canvas, fixture.Model, bitmap.Width, bitmap.Height, state);
                canvas.Flush();
                TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
                long managed = GC.GetTotalAllocatedBytes(precise: true) - allocated;
                var statistics = fixture.Data.DepthHistory.RecordingStatistics;
                long bytes = Directory.Exists(Path.Combine(bookRoot, "Depth"))
                    ? Directory.EnumerateFiles(Path.Combine(bookRoot, "Depth"), "*.depth", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) : 0;
                var available = fixture.Model.GetDepthHistory(now, end);
                Check(record ? statistics.BookRecords > 0 && bytes > 0 && available.Samples.Count > 0 : bytes == 0,
                    record ? "opted-in actual sink/writer/selected reader/heatmap path retains wide books" : "display/heatmap baseline creates no depth files without recording consent");
                using (var frame = owner.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "kvp_performance_wide_book_" + label + ".png"));
                if (record)
                {
                    using var fresh = new DepthHistory(bookInstrument);
                    fresh.Start(bookRoot, new DepthRecordingOptions(), end); // Reader-only: no live book/cache or recording permission.
                    long readAllocated = GC.GetTotalAllocatedBytes(precise: true);
                    long readStarted = Stopwatch.GetTimestamp();
                    var loaded = Await(fresh.LoadAsync(now, end), "fresh reader loads actual recorded wide books without a live cache");
                    var readElapsed = Stopwatch.GetElapsedTime(readStarted);
                    long readManaged = GC.GetTotalAllocatedBytes(precise: true) - readAllocated;
                    timings.Add(Measure("Fresh recorded-history Apply (current-thread managed allocations)", () => fresh.Apply(loaded), warmups: 0, repetitions: 1));
                    DepthHistoryQuery? query = null;
                    timings.Add(Measure("Fresh recorded-history Query (current-thread managed allocations)", () => query = fresh.Query(now, end), warmups: 0, repetitions: 1));
                    DepthHeatmapFrame? frame = null;
                    var maxAge = TimeSpan.FromSeconds(2);
                    timings.Add(Measure("Actual LiquidityHeatmapRenderer.BuildFrame from fresh disk query",
                        () => frame = LiquidityHeatmapRenderer.BuildFrame(query!, priceGrouping: 1, maxAge: maxAge, minimumTicks: -1100, maximumTicks: -900),
                        warmups: 0, repetitions: 1));
                    var projection = new DepthHeatmapProjection();
                    var viewport = new SKRect(0, 0, 1280, 720);
                    float ProjectTime(DateTime time) => (float)((time - now).TotalSeconds / (end - now).TotalSeconds * viewport.Width);
                    float ProjectTicks(decimal priceTicks) => (float)((-900m - priceTicks) / 200m * (decimal)viewport.Height);
                    Func<DateTime, float> timeProjection = ProjectTime;
                    Func<decimal, float> tickProjection = ProjectTicks;
                    timings.Add(Measure("Actual bounded DepthHeatmapProjection.Project with explicit signed-price geometry",
                        () => projection.Project(frame!, viewport, timeProjection, tickProjection, priceGrouping: 1),
                        warmups: 0, repetitions: 1));
                    Check(query!.Samples.Count > 0 && frame!.Cells.Count > 0 && projection.Spans.Count > 0,
                        "fresh Load/Apply/Query/BuildFrame/Project produce actual nonempty bounded recorded heatmap geometry");
                    var memory = fresh.MemoryUsage;
                    Check(memory.LiveBytes <= memory.LiveLimit && memory.HistoricalBytes <= memory.HistoricalLimit &&
                        memory.SelectedBytes <= memory.SelectedLimit && memory.QueuedBytes <= memory.QueueLimit,
                        "fresh reader exposes respected conservative live/history/selection/queue byte budgets");
                    if (query is null || frame is null) throw new InvalidOperationException("Actual selected-depth calculation did not produce a query and heatmap frame.");
                    depthMeasurements.Add(new
                    {
                        scope = "Independent fresh recorded-history reader, not the instrument's live/selected cache",
                        loadMilliseconds = readElapsed.TotalMilliseconds, loadProcessManagedBytes = readManaged,
                        loadedSamples = loaded.Samples.Count, loaded.IsTruncated, loaded.Message,
                        querySamples = query.Samples.Count, cells = frame.Cells.Count, spans = projection.Spans.Count,
                        frameTruncated = frame.IsTruncated, projectionTruncated = projection.IsTruncated,
                        geometry = new { viewport.Width, viewport.Height, minimumPriceTicks = -1100, maximumPriceTicks = -900, groupingTicks = 1, maxAgeSeconds = maxAge.TotalSeconds },
                        memory,
                    });
                }
                Check(peakInputQueueBytes <= fixture.Data.DepthQueueByteLimit, "actual instrument pending depth bytes stay within the published queue limit");
                depthMeasurements.Add(new
                {
                    scope = "Actual fake-feed sink + bounded live book + optional owned writer + historical selection + heatmap/chart frames",
                    recording = record, submittedBooks = books, levelsPerSide = 1000, receivedChangesPerBook = 64,
                    logicalInputRateHz = 10, realElapsedMilliseconds = elapsed.TotalMilliseconds,
                    measuredProcessManagedBytes = managed, savedBooks = statistics.BookRecords,
                    droppedRecords = fixture.Data.DepthHistory.Status.DroppedRecords,
                    retainedDiskBytes = bytes, bookFrameBytes = statistics.BookFrameBytes,
                    selectedSamples = available.Samples.Count, status = fixture.Data.DepthHistory.Status.Message,
                    peakInputQueueBytes, inputQueueByteLimit = fixture.Data.DepthQueueByteLimit,
                    memory = fixture.Data.DepthHistory.MemoryUsage,
                    note = "Process allocation includes asynchronous writer and pumped UI/background work; display-only is a resource-cost baseline, not an equivalent persistence feature. Dense-book duration depends on retained bytes and drops.",
                });
            }
            var cold = timings[2];
            var warm = timings[3];
            Console.WriteLine($"Actual CPU chart cold {cold.MedianMilliseconds:0.###} ms / {cold.MedianManagedBytes} managed B; selected {warm.MedianMilliseconds:0.###} ms / {warm.MedianManagedBytes} managed B.");
        }
        finally
        {
            App.Services.Settings.RawHistoryHours = oldHours;
            App.Services.ApplyOperationsHistorySettings();
            File.WriteAllText(Path.Combine(outDir, "kvp-performance-results.json"), JsonSerializer.Serialize(new
            {
                checks, failures, architecture = RuntimeInformation.ProcessArchitecture.ToString(), os = RuntimeInformation.OSDescription,
                renderer = "Actual production ChartRenderer with Skia raster canvas; no Metal/CUDA/ILGPU kernel",
                cpuBaseline = "Same production numeric implementation with per-frame drawing cache invalidated, not a claim to have executed an excluded reference fork or a fixed-base binary",
                decision = "Retain correct bounded CPU reference and per-chart selected caches. No measured Metal acceleration or GPU dependency is claimed.",
                evidence, timings, depthMeasurements, runtimeFixture = runtimeRoot,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failures;
    }
}
