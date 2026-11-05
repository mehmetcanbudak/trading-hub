using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Controls;
using TradingHub.App.Localization;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;

internal static class KvpStudiesScene
{
    public static int Run(MainWindow owner, Action<double> pump, string outDir)
    {
        outDir = Path.GetFullPath(outDir);
        int checks = 0, failures = 0;
        void Check(bool value, string description)
        {
            checks++;
            Console.WriteLine($"{(value ? "OK" : "FAIL")} KVP studies: {description}");
            if (!value) failures++;
        }
        var instrument = new Instrument { FeedId = "kvp-study-ui", Symbol = "STUDY-FIXTURE", TickSize = 0.25m, VolumeStep = 1 };
        using var feed = new SimulatedFeed();
        using var data = new InstrumentData(instrument, feed);
        var start = new DateTime(2025, 12, 29, 0, 0, 0, DateTimeKind.Utc);
        long id = 0;
        for (int bar = 0; bar < 384; bar++)
        {
            decimal center = bar == 160 ? 0 : bar == 180 ? -20 : 100 + (decimal)Math.Round(16 * Math.Sin(bar / 5.0));
            foreach (var (offset, volume, side) in new[] { (-1m, 3m, TradeSide.Sell), (0m, 1m, TradeSide.None), (1m, 5m, TradeSide.Buy) })
                data.Trades.Add(new Trade(++id, start.AddMinutes(bar * 30).AddSeconds(id % 20), center + offset, volume, side));
        }
        using var model = new ChartModel(data, ChartPeriod.M30, new ChartSettings { TimeZoneId = "UTC" });
        var view = new ChartView { Model = model };
        var fixture = new Window { Title = "KVP studies — real settings and isolated raw data", Width = 1380, Height = 900, Content = view };
        fixture.Show(owner); pump(0.15);
        var studies = new (Type Type, string Property)[]
        {
            (typeof(CompositeVolumeProfile), "ValueAreaPercent"), (typeof(NakedPoc), "MaximumLevels"),
            (typeof(MultiPeriodVwap), "Source"), (typeof(PriorClosingVwap), "Keep"), (typeof(RollingVwap), "Length"), (typeof(TimedSessionVwap), "AllowEstimated"),
            (typeof(SessionBackgrounds), "ShowRanges"), (typeof(KeyLevels), "ShowPriorLevels"), (typeof(RobustVolume), "Lookback"),
            (typeof(DoubleInsideDay), "ExpirySessions"), (typeof(DeltaBubbles), "Lookback"), (typeof(CumulativeDeltaCandles), "SmoothingPeriod"),
            (typeof(MarketStructure), "SwingRadius"), (typeof(HtfStructure), "UseMappedPeriods"), (typeof(TripleBollinger), "Length"), (typeof(VwapClaire), "MinimumSessions"),
            (typeof(AnchoredFlowVwap), "RetrospectiveDrawing"),
        };
        try
        {
            foreach (var (type, propertyName) in studies)
            {
                Check(App.Services.Catalog.Find(type.FullName!)?.Type == type, "real reflection catalog exposes " + type.Name);
                var indicator = (Indicator)Activator.CreateInstance(type)!;
                var slot = model.AddIndicator(indicator, applyDefaultColors: false);
                model.Update(); model.Invalidate(); view.InvalidateVisual(); pump(0.08);
                Check(slot.Error == null, type.Name + " calculates through actual ChartModel over zero/negative/session/year boundaries: " + slot.Error);
                var settings = new IndicatorsWindow(App.Services, model);
                settings.Show(fixture); pump(0.15);
                try
                {
                    var selected = settings.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(list => list.Items.OfType<ListBoxItem>().Any(item => ReferenceEquals(item.Tag, slot)));
                    Check(selected != null, type.Name + " has an actual on-chart settings row");
                    if (selected == null) continue;
                    selected.SelectedItem = selected.Items.OfType<ListBoxItem>().Single(item => ReferenceEquals(item.Tag, slot)); pump(0.08);
                    PropertyInfo property = type.GetProperty(propertyName)!;
                    string label = Lang.T(PropertyGrid.NameOf(property));
                    var grid = settings.GetVisualDescendants().OfType<PropertyGrid>().Single();
                    var row = grid.GetVisualDescendants().OfType<Grid>().FirstOrDefault(candidate => candidate.Children.OfType<TextBlock>().Any(text => text.Text == label));
                    Check(row != null, type.Name + " exposes its real editable parameter " + propertyName);
                    if (row == null) continue;
                    object? before = property.GetValue(indicator);
                    row.BringIntoView(); pump(0.05);
                    if (row.Children.OfType<NumericUpDown>().FirstOrDefault() is { } number)
                    {
                        decimal? saved = number.Value;
                        decimal value = saved ?? 0;
                        number.Value = value < number.Maximum ? value + Math.Max(1, number.Increment) : value - Math.Max(1, number.Increment);
                        model.Update(); pump(0.05);
                        Check(!Equals(before, property.GetValue(indicator)), type.Name + " numeric control changes the real indicator instance");
                        number.Value = saved;
                    }
                    else if (row.Children.OfType<ComboBox>().FirstOrDefault() is { } combo)
                    {
                        object? saved = combo.SelectedItem;
                        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().First(item => !Equals(item.Tag, before));
                        model.Update(); pump(0.05);
                        Check(!Equals(before, property.GetValue(indicator)), type.Name + " enum control changes calculation selection");
                        combo.SelectedItem = saved;
                    }
                    else if (row.Children.OfType<CheckBox>().FirstOrDefault() is { } check)
                    {
                        bool? saved = check.IsChecked;
                        check.IsChecked = !(saved == true); model.Update(); pump(0.05);
                        Check(!Equals(before, property.GetValue(indicator)), type.Name + " checkbox changes the actual configured capability");
                        check.IsChecked = saved;
                    }
                    else Check(false, type.Name + " has the expected real parameter editor");
                    model.Update(); model.Invalidate(); view.InvalidateVisual(); pump(0.05);
                    Check(Equals(before, property.GetValue(indicator)) && slot.Error == null, type.Name + " editor restores the original setting and recalculates without error");
                    using (var frame = settings.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "kvp_study_settings_" + type.Name + ".png"));
                }
                finally { settings.Close(); }
                view.InvalidateVisual(); pump(0.05);
                using (var frame = fixture.CaptureRenderedFrame())
                {
                    Check(frame != null, type.Name + " produces an actual rendered chart frame");
                    frame?.Save(Path.Combine(outDir, "kvp_study_" + type.Name + ".png"));
                }
                model.RemoveIndicator(slot);
            }

            using var prices = new InstrumentData(instrument, feed);
            DateTime jan = new(2026, 1, 31, 12, 0, 0, DateTimeKind.Utc);
            prices.Trades.Add(new Trade(1, jan, 10, 1, TradeSide.Buy));
            prices.Trades.Add(new Trade(2, jan.AddMinutes(1), 20, 3, TradeSide.Buy));
            prices.Trades.Add(new Trade(3, new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc), 100, 2, TradeSide.Buy));
            using var vwapModel = new ChartModel(prices, ChartPeriod.M1, new ChartSettings { TimeZoneId = "UTC" });
            var simultaneous = new MultiPeriodVwap(); var vwapSlot = vwapModel.AddIndicator(simultaneous);
            Check(vwapSlot.Error == null && ((OptionalValueDataSeries)simultaneous.DataSeries[2])[1] == 17.5m,
                "independent raw weighted prices 10@1 +20@3 give month VWAP17.5");
            Check(((OptionalValueDataSeries)simultaneous.DataSeries[2])[2] == 100m && ((OptionalValueDataSeries)simultaneous.DataSeries[4])[2] == 45m,
                "calendar February resets month100 while same year remains45");

            using var deltaData = new InstrumentData(instrument, feed);
            deltaData.Trades.Add(new Trade(1, jan, 10, 2, TradeSide.Buy));
            deltaData.Trades.Add(new Trade(2, jan.AddMinutes(1), 10, 1, TradeSide.Sell));
            deltaData.Trades.Add(new Trade(3, jan.AddMinutes(2), 10, 5, TradeSide.Buy));
            using var deltaModel = new ChartModel(deltaData, ChartPeriod.M1, new ChartSettings { TimeZoneId = "UTC" });
            var cvd = new CumulativeDeltaCandles { SmoothingPeriod = 2, Style = CumulativeDeltaCandles.CvdStyle.Line };
            var deltaSlot = deltaModel.AddIndicator(cvd);
            Check(deltaSlot.Error == null && ((OptionalValueDataSeries)cvd.DataSeries[0])[2] == 6m && ((OptionalValueDataSeries)cvd.DataSeries[2])[2] == 3.5m,
                "independent +2,-1,+5 prints yield CVD6 and two-bar simple smoothing3.5");
            foreach (var style in Enum.GetValues<CumulativeDeltaCandles.CvdStyle>())
            {
                cvd.Style = style; deltaModel.Update();
                Check(((OptionalValueDataSeries)cvd.DataSeries[0])[2] == 6m && deltaSlot.Error == null, "CVD visualization preserves causal value in " + style);
            }

            using var flowData = new InstrumentData(instrument, feed);
            decimal[] flowPrices = [5m, 4m, 2m, 4m, 5m, 6m];
            for (int i = 0; i < flowPrices.Length; i++)
                flowData.Trades.Add(new Trade(i + 1, jan.AddMinutes(i), flowPrices[i], 1, TradeSide.Buy));
            using var flowModel = new ChartModel(flowData, ChartPeriod.M1, new ChartSettings { TimeZoneId = "UTC" });
            var flow = new AnchoredFlowVwap { Mode = AnchoredFlowVwap.AnchorMode.Swing, Swing = AnchoredFlowVwap.SwingKind.Low,
                SwingRadius = 1, RetrospectiveDrawing = true };
            var flowSlot = flowModel.AddIndicator(flow);
            var causal = (OptionalValueDataSeries)flow.DataSeries[0];
            Check(flowSlot.Error == null && flowModel.Series[3].IsClosed && !causal.IsAvailable(2) &&
                causal.IsAvailable(3) && causal[3] == 3m && flow.RetrospectiveValueAt(2) == 2m && flow.RetrospectiveConfirmationAt(2) == 3,
                "actual historical Flow consumer confirms pivot2 only at right-bar3 close (minute4), publishes (2+4)/2=3 and leaves the earlier pivot's strategy value unavailable");
            Check(causal.IsAvailable(4) && Math.Abs(causal[4] - 11m / 3m) < 0.00000000000000000001m &&
                causal.IsAvailable(5) && Math.Abs(causal[5] - 4.25m) < 0.00000000000000000001m,
                "confirmed Flow values independently conserve 2,4,5,6 equal-weight prices while earlier bars stay missing");
            var beforeVisualChange = Enumerable.Range(0, flowPrices.Length).Select(i => (causal.IsAvailable(i), Value: causal[i])).ToArray();
            flow.RetrospectiveDrawing = false; flowModel.Update();
            Check(Enumerable.Range(0, flowPrices.Length).All(i => beforeVisualChange[i] == (causal.IsAvailable(i), causal[i])),
                "changing drawing-only retrospective presentation cannot change causal strategy series");
            view.Model = flowModel; view.InvalidateVisual(); pump(0.15);
            using (var frame = fixture.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "kvp_study_flow_causal_primary.png"));
            flow.RetrospectiveDrawing = true; flowModel.Update(); view.InvalidateVisual(); pump(0.15);
            using (var frame = fixture.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "kvp_study_flow_retrospective_drawing_only.png"));
            view.Model = model;
        }
        finally { fixture.Close(); }
        File.WriteAllText(Path.Combine(outDir, "kvp-studies-results.json"), System.Text.Json.JsonSerializer.Serialize(new { checks, failures }));
        return failures;
    }
}
