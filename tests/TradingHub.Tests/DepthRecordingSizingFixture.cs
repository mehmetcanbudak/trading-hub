using System.Diagnostics;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>Fake-only sizing input; measures the real framed recorder, not a hypothetical bytes-per-level formula.</summary>
public static class DepthRecordingSizingFixture
{
    public sealed record Measurement(int LevelsPerSide, int ChangesPerUpdate, int SubmittedBooks, long SavedBooks, long DroppedRecords,
        long BookFrameBytes, long GapFrameBytes, long HeaderBytes, long RetainedDiskBytes, TimeSpan Elapsed,
        double SavedBooksPerSecond, double MeanBookFrameBytes, double ProjectedBytesPerDay, double QuotaMinutesAtRequestedRate,
        int RecordedBidLevels, int RecordedAskLevels, int RecordedChanges, DepthCoverage RecordedCoverage, long LargestEstimatedSampleBytes);

    public static DepthUpdate WideBook(DateTime time, int levelsPerSide, int changes, long sequence, decimal bestBidSize = 1.25m)
    {
        if (levelsPerSide is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(levelsPerSide));
        if (changes is < 0 or > 100_000) throw new ArgumentOutOfRangeException(nameof(changes));
        var bids = new BookLevel[levelsPerSide];
        var asks = new BookLevel[levelsPerSide];
        for (int i = 0; i < levelsPerSide; i++)
        {
            bids[i] = new(-1000 - i, bestBidSize + i % 17 / 100m);
            asks[i] = new(-999 + i, 2.50m + i % 19 / 100m);
        }
        var received = new BookChange[changes];
        for (int i = 0; i < changes; i++)
        {
            int level = i / 2 % levelsPerSide;
            var side = i % 2 == 0 ? BookSide.Bid : BookSide.Ask;
            var source = side == BookSide.Bid ? bids[level] : asks[level];
            received[i] = new(side, source.PriceTicks, source.Size);
        }
        return new DepthUpdate
        {
            Snapshot = new DepthSnapshot(bids, asks, time.AddMilliseconds(-1)), Changes = received,
            ProviderId = "fake", Venue = "wide-book-fixture", ReceivedAt = time, Sequence = sequence,
            IsFullRefresh = true, Coverage = DepthCoverage.ReceivedChanges | DepthCoverage.PartialBook,
            Message = "Fake bounded L2 book; neither event-complete nor market-by-order.",
        };
    }

    /// <summary>
    /// Caller supplies a fresh fixture root. producerDelay=Zero measures a burst and reports drops;
    /// a paced run measures the requested input rate without claiming event-complete persistence.
    /// RequestedRate only projects measured frame bytes; it is not a throughput certification.
    /// </summary>
    public static async Task<Measurement> MeasureAsync(string root, int levelsPerSide = 1000, int changesPerUpdate = 64,
        int books = 200, double requestedRate = 10, TimeSpan producerDelay = default, CancellationToken ct = default)
    {
        if (books < 1 || !double.IsFinite(requestedRate) || requestedRate <= 0) throw new ArgumentOutOfRangeException(nameof(books));
        if (Directory.Exists(root)) throw new IOException("The sizing fixture requires a fresh root.");
        var instrument = new Instrument { FeedId = "fake", Symbol = "DEPTH-SIZING-FIXTURE", Exchange = "wide-book-fixture", TickSize = 0.25m, VolumeStep = 0.01m };
        var options = new DepthRecordingOptions { Enabled = true };
        var time = new DateTime(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);
        using var history = new DepthHistory(instrument);
        history.Start(root, options, time);
        var clock = Stopwatch.StartNew();
        DepthUpdate? normalized = null;
        long largestSample = 0;
        for (int i = 0; i < books; i++)
        {
            ct.ThrowIfCancellationRequested();
            var at = time.AddSeconds(i / requestedRate);
            normalized = history.Receive(WideBook(at, levelsPerSide, changesPerUpdate, i + 1), at);
            largestSample = Math.Max(largestSample, DepthSample.EstimateMemoryBytes(normalized));
            if (producerDelay > TimeSpan.Zero) await Task.Delay(producerDelay, ct);
        }
        history.Stop(time.AddSeconds(books / requestedRate));
        clock.Stop();
        if (history.Status.RecordingState == DepthRecordingState.Error) throw new IOException(history.Status.Message);
        var statistics = history.RecordingStatistics;
        long diskBytes = Directory.EnumerateFiles(Path.Combine(root, "Depth"), "*.depth", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        double mean = statistics.BookRecords == 0 ? 0 : (double)statistics.BookFrameBytes / statistics.BookRecords;
        var final = normalized!;
        return new(levelsPerSide, changesPerUpdate, books, statistics.BookRecords, history.Status.DroppedRecords,
            statistics.BookFrameBytes, statistics.GapFrameBytes, statistics.HeaderBytes, diskBytes, clock.Elapsed,
            statistics.BookRecords / Math.Max(0.000001, clock.Elapsed.TotalSeconds), mean,
            mean * requestedRate * 86_400, mean == 0 ? 0 : options.MaxDiskBytes / (mean * requestedRate * 60),
            final.Snapshot.Bids.Length, final.Snapshot.Asks.Length, final.Changes.Count, final.Coverage, largestSample);
    }
}
