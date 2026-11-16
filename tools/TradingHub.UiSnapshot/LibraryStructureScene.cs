using System.ComponentModel;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using TradingHub.App;
using TradingHub.App.Controls;
using TradingHub.App.Localization;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;

/// <summary>
/// The structure, sessions, volume-profile and VWAP library studies through the real catalog, library groups, chart
/// renderer and alert-level hook. Every closed-bar library study renders once on an isolated simulated market that crosses
/// the 2026 US and UK daylight-saving changes. Repeat with TRADINGHUB_LANG=es and zh-CN.
/// </summary>
internal static class LibraryStructureScene
{
    private static readonly string[] LibraryGroups =
        { IndicatorCategories.Structure, IndicatorCategories.Sessions, IndicatorCategories.VolumeProfile, IndicatorCategories.Vwap };

    public static int Run(MainWindow owner, Action<double> pump, string outDir)
    {
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        int checks = 0, failures = 0;
        void Check(bool value, string description)
        {
            checks++;
            Console.WriteLine($"{(value ? "OK" : "FAIL")} Library structure: {description}");
            if (!value) failures++;
        }
        string language = Lang.Current;
        var instrument = new Instrument { FeedId = "library-structure-ui", Symbol = "LIBRARY-FIXTURE", TickSize = 0.25m, VolumeStep = 1 };
        using var feed = new SimulatedFeed();
        using var data = new InstrumentData(instrument, feed);
        // 2026-03-05 .. 2026-04-01 on 15-minute bars (weekends left empty): crosses US DST (03-08) and UK DST (03-29)
        var start = new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc);
        long id = 0; var random = new Random(11); decimal price = 100;
        for (int bar = 0; bar < 27 * 96; bar++)
        {
            var time = start.AddMinutes(15 * bar);
            if (time.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            price = Math.Max(20, price + (decimal)Math.Round(1.5 * Math.Sin(bar / 23.0) + (random.NextDouble() - 0.5) * 2, 2));
            decimal p = Math.Round(price * 4) / 4;
            foreach (var (offset, volume, side) in new[] { (-0.75m, 3m, TradeSide.Sell), (0m, 2m + bar % 7, TradeSide.Buy), (0.5m, 4m, TradeSide.Buy), (-0.25m, 1m, TradeSide.Sell) })
                data.Trades.Add(new Trade(++id, time.AddSeconds(1 + id % 50), p + offset, volume, side));
        }
        using var model = new ChartModel(data, ChartPeriod.M15, new ChartSettings { TimeZoneId = "UTC" });
        var view = new ChartView { Model = model };
        var fixture = new Window { Title = "Structure, sessions and profile library — isolated fixture", Width = 1380, Height = 900, Content = view };
        fixture.Show(owner); pump(0.15);
        try
        {
            var items = App.Services.Catalog.Items;
            var studies = items.Where(i => typeof(ClosedBarStudy).IsAssignableFrom(i.Type) && !i.Type.IsAbstract).ToList();
            Check(studies.Count >= 20, $"the real catalog lists the closed-bar library studies ({studies.Count})");
            var groups = IndicatorLibrary.Build(items, new IndicatorLibraryLayout());
            foreach (var info in studies)
            {
                var type = info.Type;
                var group = groups.FirstOrDefault(g => g.Items.Contains(info));
                Check(group != null && LibraryGroups.Contains(group.Id), $"{type.Name} is in a structure/sessions/profile/VWAP library group ({group?.Id})");
                string english = type.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? type.Name;
                string description = type.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
                Check(!english.Contains("KVP", StringComparison.OrdinalIgnoreCase) && description.Length > 40, $"{type.Name} has a neutral English name and an explanatory description");
                if (language != Lang.English)
                    Check(Lang.T(english) != english && Lang.T(description) != description, $"{type.Name} name and description are translated in {language}: {Lang.T(english)}");

                var indicator = (ClosedBarStudy)Activator.CreateInstance(type)!;
                var slot = model.AddIndicator(indicator, applyDefaultColors: false);
                model.Update(); model.Invalidate(); view.InvalidateVisual(); pump(0.08);
                Check(slot.Error == null, $"{type.Name} calculates through the actual ChartModel across DST changes: {slot.Error}");
                Check(indicator.Bars.Count >= model.Series.Count - 1 && indicator.Bars.Count <= model.Series.Count, $"{type.Name} processed every completed bar and never the forming one");
                Check(indicator.Scene.Boxes.All(s => s.From <= s.Available) && indicator.Scene.Lines.All(s => s.From <= s.Available) && indicator.Scene.Marks.All(m => m.Bar <= m.Available),
                    $"{type.Name} draws nothing before the bar it was detected from");
                var levels = indicator.GetAlertLevels().ToList();
                Check(levels.Select(l => l.Id).Distinct().Count() == levels.Count, $"{type.Name} alert levels have unique ids ({levels.Count})");
                if (language != Lang.English && !string.IsNullOrEmpty(indicator.Scene.DashTitle))
                    Check(Lang.T(indicator.Scene.DashTitle) != indicator.Scene.DashTitle, $"{type.Name} dashboard title is translated in {language}");
                using (var frame = fixture.CaptureRenderedFrame())
                {
                    Check(frame != null, $"{type.Name} renders in an actual chart frame");
                    frame?.Save(Path.Combine(outDir, $"library_structure_{type.Name}_{language}.png"));
                }
                Check(slot.Error == null, $"{type.Name} renders without error: {slot.Error}");
                model.RemoveIndicator(slot);
            }

            // studies extended (not replaced) by this library: new options on the existing persistent types
            var keyLevels = new KeyLevels { ShowPriorClose = true, ShowLevelLabels = true };
            var multi = new MultiPeriodVwap { SessionBands = true, WeekBands = true, ShowSecondBand = true, ShowPriceLabels = true };
            var timed = new TimedSessionVwap { Preset = TimedSessionVwap.SessionPreset.London };
            foreach (var (indicator, seriesId) in new (Indicator, string)[] { (keyLevels, "KeyLevels.Day.PriorClose"), (multi, "MultiVwap.Day.Upper2"), (timed, "") })
            {
                var slot = model.AddIndicator(indicator, applyDefaultColors: false);
                model.Update(); model.Invalidate(); view.InvalidateVisual(); pump(0.08);
                string name = indicator.GetType().Name;
                Check(slot.Error == null, $"{name} with its new options calculates: {slot.Error}");
                if (seriesId.Length > 0)
                {
                    var series = indicator.DataSeries.OfType<OptionalValueDataSeries>().SingleOrDefault(s => s.Id == seriesId);
                    Check(series != null && Enumerable.Range(0, model.Series.Count).Any(series.IsAvailable), $"{name} publishes {seriesId} after its source period completes");
                }
                if (indicator is KeyLevels levels)
                    Check(levels.LevelLabels(model.Series.Count - 1).Count > 0, "Key levels name the levels at the right edge");
                using (var frame = fixture.CaptureRenderedFrame())
                {
                    Check(frame != null, $"{name} renders its new options in an actual chart frame");
                    frame?.Save(Path.Combine(outDir, $"library_structure_{name}_{language}.png"));
                }
                model.RemoveIndicator(slot);
            }
        }
        finally { fixture.Close(); }
        File.WriteAllText(Path.Combine(outDir, $"library-structure-results-{language}.json"), System.Text.Json.JsonSerializer.Serialize(new { checks, failures, language }));
        return failures;
    }
}
