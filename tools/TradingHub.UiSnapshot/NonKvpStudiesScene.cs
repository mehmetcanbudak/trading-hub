using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Controls;
using TradingHub.App.Windows;
using TradingHub.App.Localization;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;

internal static class NonKvpStudiesScene
{
    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        int failures = 0;
        void Check(bool condition, string description)
        {
            Console.WriteLine($"{(condition ? "OK" : "FAIL")} studies: {description}");
            if (!condition) failures++;
        }
        foreach (string evidence in new[] { "observed", "estimated", "missing", "zero-gaps" })
        {
            var instrument = new Instrument { Symbol = "STUDIES-" + evidence.ToUpperInvariant(), FeedId = "sim", TickSize = 1, VolumeStep = 1 };
            var data = new InstrumentData(instrument, new SimulatedFeed());
            var start = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
            long id = 1;
            for (int bar = 0; bar < 336; bar++)
            {
                decimal center = 1000 + (decimal)Math.Round(22 * Math.Sin(bar * Math.PI / 12));
                var time = start.AddMinutes(bar * 30);
                foreach (var (offset, side, volume) in new[] { (-3, TradeSide.Sell, 75m), (0, TradeSide.None, 20m), (3, TradeSide.Buy, 65m), (1, TradeSide.Buy, 12m) })
                {
                    long tradeId = evidence == "observed" || evidence == "zero-gaps" && bar >= 48 ? id : -id;
                    data.Trades.Add(new Trade(tradeId, time.AddSeconds(id++ % 20), evidence == "zero-gaps" ? 0 : center + offset, volume, side));
                }
            }
            using var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
            bool estimate = evidence == "estimated";
            var flow = new AnchoredFlowVwap { Mode = evidence == "zero-gaps" ? AnchoredFlowVwap.AnchorMode.Manual : AnchoredFlowVwap.AnchorMode.Swing, SwingRadius = 2, Swing = AnchoredFlowVwap.SwingKind.Low, Weight = AnchoredFlowVwap.FlowWeight.Volume, AllowEstimated = estimate };
            var refill = new RefillZones { MinimumVolume = 25, PriceGrouping = 2, AbsorptionOnly = false, AllowEstimated = estimate, MaximumLifetime = 40 };
            var profile = new CompositeVolumeProfile { Period = CompositeVolumeProfile.ProfilePeriod.Session, PriceGrouping = 2, AllowEstimated = estimate, ShowTestedPocs = true };
            Indicator[] studies = { flow, refill, profile,
                new QtMovingAverage { Period = 12 },
                new QtMaRibbon { FastPeriod = 9, MediumPeriod = 20, SlowPeriod = 30 },
                new QtCamarilla() };
            var slots = studies.Select(study => model.AddIndicator(study, applyDefaultColors: false)).ToArray();
            Check(slots.All(slot => slot.Error == null), evidence + ": all six real studies calculate without errors: " + string.Join("; ", slots.Where(s => s.Error != null).Select(s => s.Error)));
            Check(evidence == "zero-gaps" ? flow.AnchorBar == 0 : flow.AnchorBar >= 0 && flow.ConfirmationBar > flow.AnchorBar, evidence + ": explicit history start or closed retrospective swing");
            Check(evidence == "missing" ? !flow.IsAvailable && refill.Zones.Count == 0 && profile.Profiles.All(p => p.Total == 0)
                : flow.IsAvailable && refill.Zones.Count > 0 && profile.Profiles.Any(p => p.Total > 0), evidence + ": calculations reflect actual evidence availability");
            Check(evidence switch { "observed" => flow.Coverage.HasFlag(StudyCoverage.Observed), "estimated" => flow.Coverage.HasFlag(StudyCoverage.Estimated), _ => flow.Coverage.HasFlag(StudyCoverage.MissingBars) }, evidence + ": fidelity is textual and truthful");
            if (evidence == "zero-gaps")
            {
                var series = (OptionalValueDataSeries)flow.DataSeries[0];
                Check(!series.IsAvailable(0) && series.IsAvailable(48) && series[48] == 0 && series.IsAvailable(335) && series[335] == 0,
                    "actual Flow VWAP preserves missing gaps followed by available zero prices");
            }
            var view = new ChartView { Model = model };
            model.Viewport.BarWidth = 7;
            if (evidence == "zero-gaps") model.Viewport.BarWidth = 3;
            model.Viewport.ScrollBars = 0;
            var text = new TextBlock { Text = $"Actual six-study calculations — {evidence}\n{flow.Status}\n{refill.Status}\n{profile.Status}", Foreground = Brushes.White, Margin = new Avalonia.Thickness(12), TextWrapping = TextWrapping.Wrap };
            var panel = new DockPanel(); DockPanel.SetDock(text, Dock.Top); panel.Children.Add(text); panel.Children.Add(view);
            var fixture = new Window { Title = "Non-KVP studies — isolated fake fixture", Width = 1400, Height = 900, Background = new SolidColorBrush(Color.Parse("#151923")), Content = panel };
            fixture.Show(window); model.Invalidate(); pump(1);
            using (var frame = fixture.CaptureRenderedFrame())
            {
                Check(frame != null, evidence + ": chart renders an actual frame");
                frame?.Save(Path.Combine(outDir, $"nonkvp_studies_{evidence}.png"));
            }
            if (evidence == "zero-gaps")
            {
                foreach (var slot in slots.Skip(1)) slot.Visible = false;
                model.Invalidate(); view.InvalidateVisual(); pump(0.2);
                string withPath = Path.Combine(outDir, "nonkvp_studies_zero_line.png"), withoutPath = Path.Combine(outDir, "nonkvp_studies_zero_line_hidden.png");
                using (var frame = fixture.CaptureRenderedFrame()) frame?.Save(withPath);
                slots[0].Visible = false; model.Invalidate(); view.InvalidateVisual(); pump(0.2);
                using (var frame = fixture.CaptureRenderedFrame()) frame?.Save(withoutPath);
                if (File.Exists(withPath) && File.Exists(withoutPath))
                {
                    using var shown = SkiaSharp.SKBitmap.Decode(withPath);
                    using var hidden = SkiaSharp.SKBitmap.Decode(withoutPath);
                    int changed = 0;
                    for (int y = 200; y < Math.Min(shown.Height, hidden.Height) - 40; y++)
                    for (int x = 100; x < Math.Min(shown.Width, hidden.Width) - 100; x++)
                        if (shown.GetPixel(x, y) != hidden.GetPixel(x, y)) changed++;
                    Check(changed > 20, "available zero-price Flow VWAP changes actual plot pixels, not only legend text");
                }
                else Check(false, "zero-price rendered comparison frames exist");
                foreach (var slot in slots) slot.Visible = true;
                model.Invalidate(); view.InvalidateVisual(); pump(0.2);
            }
            if (evidence == "observed")
            {
                foreach (var slot in slots.Skip(1)) slot.Visible = false;
                int originalFill = flow.FillOpacity; bool originalBubbles = flow.ShowFlowBubbles;
                flow.FillOpacity = 0; flow.ShowFlowBubbles = false;
                string? previous = null;
                foreach (int bands in new[] { 0, 1, 3 })
                {
                    flow.Bands = bands; model.Update(); model.Invalidate(); view.InvalidateVisual(); pump(0.2);
                    Check(slots[0].Error == null, $"band count {bands} recalculates without error");
                    int lastBar = model.Series.Count - 1;
                    Check(flow.DataSeries.Skip(1).Cast<IValueDataSeriesAvailability>().Count(series => series.IsAvailable(lastBar)) == bands * 2,
                        $"band count {bands} exposes exactly {bands * 2} renderer-consumable band outputs");
                    string path = Path.Combine(outDir, $"nonkvp_studies_bands_{bands}.png");
                    using (var frame = fixture.CaptureRenderedFrame()) frame?.Save(path);
                    if (File.Exists(path))
                    {
                        using var current = SkiaSharp.SKBitmap.Decode(path);
                        int bandInk = 0, changed = 0;
                        using var before = previous != null && File.Exists(previous) ? SkiaSharp.SKBitmap.Decode(previous) : null;
                        for (int y = 200; y < current.Height - 40; y++)
                        for (int x = 100; x < current.Width - 100; x++)
                        {
                            var pixel = current.GetPixel(x, y);
                            if (Math.Abs(pixel.Red - 184) < 12 && Math.Abs(pixel.Green - 134) < 12 && Math.Abs(pixel.Blue - 11) < 12) bandInk++;
                            if (before != null && x < before.Width && y < before.Height && pixel != before.GetPixel(x, y)) changed++;
                        }
                        Check(bands == 0 ? bandInk == 0 : bandInk > 20, $"band count {bands} has correct actual band-line plot ink (fills and bubbles off)");
                        if (previous != null) Check(changed > 20, $"changing to {bands} bands changes actual plot pixels");
                    }
                    else Check(false, $"band count {bands} rendered frame exists");
                    previous = path;
                }
                flow.FillOpacity = originalFill; flow.ShowFlowBubbles = originalBubbles;
                foreach (var slot in slots) slot.Visible = true;
                model.Invalidate(); view.InvalidateVisual(); pump(0.2);
            }
            var settings = new IndicatorsWindow(App.Services, model);
            settings.Show(fixture); pump(0.3);
            var onChart = settings.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(list => list.Items.OfType<ListBoxItem>().Any(item => item.Tag is IndicatorSlot));
            Check(onChart != null, evidence + ": real indicator settings list opens");
            if (onChart != null)
            {
                TEditor? ParameterEditor<TEditor>(string sourceLabel) where TEditor : Control
                {
                    // Localized text is a locator, never an expected-language assertion.
                    var grid = settings.GetVisualDescendants().OfType<PropertyGrid>().Single();
                    var row = grid.GetVisualDescendants().OfType<Grid>()
                        .FirstOrDefault(candidate => candidate.Children.OfType<TextBlock>().Any(label => label.Text == Lang.T(sourceLabel)));
                    var editor = row?.Children.OfType<TEditor>().SingleOrDefault();
                    Check(editor != null, evidence + ": selected study opens an editable parameter: " + sourceLabel);
                    editor?.BringIntoView(); pump(0.05);
                    return editor;
                }
                void RecalculateFromEditor()
                {
                    model.Update(); view.InvalidateVisual(); pump(0.05);
                    Check(slots.All(candidate => candidate.Error == null), evidence + ": settings interaction recalculates without indicator errors");
                }
                foreach (var slot in slots)
                {
                    onChart.SelectedItem = onChart.Items.OfType<ListBoxItem>().First(item => ReferenceEquals(item.Tag, slot)); pump(0.1);
                    switch (slot.Indicator)
                    {
                        case AnchoredFlowVwap selectedFlow:
                        {
                            var editor = ParameterEditor<NumericUpDown>("Sigma bands");
                            if (editor == null) break;
                            decimal? original = editor.Value;
                            editor.Value = 1; RecalculateFromEditor();
                            int availableBands = selectedFlow.DataSeries.Skip(1).Cast<IValueDataSeriesAvailability>()
                                .Count(series => series.IsAvailable(model.Series.Count - 1));
                            Check(selectedFlow.Bands == 1 && availableBands == (selectedFlow.IsAvailable ? 2 : 0),
                                evidence + ": Flow band editor changes actual available band outputs without inventing missing data");
                            editor.Value = original; RecalculateFromEditor();
                            Check(selectedFlow.Bands == original && selectedFlow.DataSeries.Skip(1).Cast<IValueDataSeriesAvailability>()
                                .Count(series => series.IsAvailable(model.Series.Count - 1)) == (selectedFlow.IsAvailable ? 6 : 0),
                                evidence + ": Flow editor restores the original three-band calculation");
                            break;
                        }
                        case RefillZones selectedRefill:
                        {
                            var editor = ParameterEditor<NumericUpDown>("Minimum bucket volume");
                            if (editor == null) break;
                            decimal? original = editor.Value; int originalCount = selectedRefill.Zones.Count;
                            editor.Value = 1000; RecalculateFromEditor();
                            Check(selectedRefill.MinimumVolume == 1000 && selectedRefill.Zones.Count == 0,
                                evidence + ": refill volume editor filters actual calculated zones");
                            editor.Value = original; RecalculateFromEditor();
                            Check(selectedRefill.Zones.Count == originalCount, evidence + ": refill editor restores all original zones");
                            break;
                        }
                        case CompositeVolumeProfile selectedProfile:
                        {
                            var editor = ParameterEditor<NumericUpDown>("Value area (%)");
                            if (editor == null) break;
                            decimal? original = editor.Value; decimal originalTotal = selectedProfile.Profiles.Sum(block => block.Total);
                            var originalAreas = selectedProfile.Profiles.Select(block => (block.ValueAreaLow, block.ValueAreaHigh, block.Total)).ToArray();
                            editor.Value = 100; RecalculateFromEditor();
                            Check(selectedProfile.ValueAreaPercent == 100 && selectedProfile.Profiles.Sum(block => block.Total) == originalTotal &&
                                selectedProfile.Profiles.All(block => block.Total == 0 ||
                                    block.ValueAreaLow == block.Levels.Keys.Min() && block.ValueAreaHigh == block.Levels.Keys.Max()),
                                evidence + ": profile value-area editor expands actual available profiles without changing volume");
                            editor.Value = original; RecalculateFromEditor();
                            Check(originalAreas.SequenceEqual(selectedProfile.Profiles.Select(block => (block.ValueAreaLow, block.ValueAreaHigh, block.Total))),
                                evidence + ": profile editor restores original calculated value areas");
                            break;
                        }
                        case QtMovingAverage selectedAverage:
                        {
                            var editor = ParameterEditor<NumericUpDown>("Period");
                            if (editor == null) break;
                            decimal? original = editor.Value; int lastBar = model.Series.Count - 1;
                            var output = (OptionalValueDataSeries)selectedAverage.DataSeries[0]; decimal originalValue = output[lastBar];
                            Check(output.IsAvailable(lastBar), evidence + ": moving average has a ready output before editing");
                            editor.Value = 1000; RecalculateFromEditor();
                            Check(!output.IsAvailable(lastBar) && !selectedAverage.IsReady,
                                evidence + ": moving-average period editor exposes genuine library warmup");
                            editor.Value = original; RecalculateFromEditor();
                            Check(output.IsAvailable(lastBar) && output[lastBar] == originalValue,
                                evidence + ": moving-average editor restores the original calculated output");
                            break;
                        }
                        case QtMaRibbon selectedRibbon:
                        {
                            var editor = ParameterEditor<NumericUpDown>("Slow period");
                            if (editor == null) break;
                            decimal? original = editor.Value; int lastBar = model.Series.Count - 1;
                            var slow = (OptionalValueDataSeries)selectedRibbon.DataSeries[2]; decimal originalValue = slow[lastBar];
                            bool fastReady = selectedRibbon.FastIsReady, mediumReady = selectedRibbon.MediumIsReady;
                            Check(slow.IsAvailable(lastBar), evidence + ": ribbon slow line has a ready output before editing");
                            editor.Value = 1000; RecalculateFromEditor();
                            Check(!slow.IsAvailable(lastBar) && selectedRibbon.FastIsReady == fastReady && selectedRibbon.MediumIsReady == mediumReady,
                                evidence + ": ribbon slow-period editor changes only the selected line's warmup");
                            editor.Value = original; RecalculateFromEditor();
                            Check(slow.IsAvailable(lastBar) && slow[lastBar] == originalValue,
                                evidence + ": ribbon editor restores the original slow-line calculation");
                            break;
                        }
                        case QtCamarilla selectedCamarilla:
                        {
                            var editor = ParameterEditor<ComboBox>("Session");
                            var zoneEditor = ParameterEditor<TextBox>("Custom time zone");
                            var hourEditor = ParameterEditor<NumericUpDown>("Custom start hour");
                            if (editor == null || zoneEditor == null || hourEditor == null) break;
                            object? original = editor.SelectedItem;
                            string? originalZone = zoneEditor.Text; decimal? originalHour = hourEditor.Value;
                            var output = (ValueDataSeries)selectedCamarilla.DataSeries[0];
                            bool[] originalBreaks = Enumerable.Range(0, model.Series.Count).Select(output.IsThisPointOfEndLine).ToArray();
                            int customBoundary = Enumerable.Range(1, model.Series.Count - 1)
                                .First(bar => model.Series[bar].Time.Hour == 7 && model.Series[bar].Time.Minute == 0);
                            editor.SelectedItem = editor.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, CamarillaSessionMode.Custom));
                            zoneEditor.Text = "UTC";
                            zoneEditor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = zoneEditor, Key = Key.Enter });
                            zoneEditor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Source = zoneEditor, Key = Key.Enter });
                            hourEditor.Value = 7;
                            RecalculateFromEditor();
                            Check(selectedCamarilla.SessionMode == CamarillaSessionMode.Custom && selectedCamarilla.TimeZoneId == "UTC" && selectedCamarilla.SessionStartHour == 7 &&
                                !originalBreaks.SequenceEqual(Enumerable.Range(0, model.Series.Count).Select(output.IsThisPointOfEndLine)),
                                evidence + ": Camarilla session editor changes actual rendered session-break positions");
                            Check(!originalBreaks[customBoundary - 1] && output.IsThisPointOfEndLine(customBoundary - 1),
                                evidence + ": custom UTC 07:00 session creates its independently expected renderer break");
                            editor.SelectedItem = original;
                            zoneEditor.Text = originalZone;
                            zoneEditor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = zoneEditor, Key = Key.Enter });
                            zoneEditor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Source = zoneEditor, Key = Key.Enter });
                            hourEditor.Value = originalHour; RecalculateFromEditor();
                            Check(selectedCamarilla.SessionMode == CamarillaSessionMode.Chart && selectedCamarilla.TimeZoneId == originalZone &&
                                originalHour is decimal restoredHour && selectedCamarilla.SessionStartHour == (double)restoredHour &&
                                originalBreaks.SequenceEqual(Enumerable.Range(0, model.Series.Count).Select(output.IsThisPointOfEndLine)),
                                evidence + ": Camarilla editor restores chart-session output boundaries");
                            break;
                        }
                    }
                }
                onChart.SelectedItem = onChart.Items.OfType<ListBoxItem>().First(item => ReferenceEquals(item.Tag, slots[0])); pump(0.2);
            }
            using (var frame = settings.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, $"nonkvp_studies_{evidence}_settings.png"));
            settings.Close();
            profile.Period = CompositeVolumeProfile.ProfilePeriod.Month;
            model.SetPeriod(new ChartPeriod(PeriodType.Minutes, 5));
            model.Update();
            model.CenterOn(model.Series[0].Time, model.Series[model.Series.Count - 1].Time);
            model.Invalidate(); view.InvalidateVisual(); pump(0.4);
            Check(slots.All(slot => slot.Error == null) && profile.RawProfileCount == 2, evidence + ": month boundary and chart period recalculation use host sessions");
            Check(model.LastLayout is { } monthLayout && monthLayout.FirstVisible == 0 && monthLayout.LastVisible == model.Series.Count - 1,
                evidence + ": month capture frames the full loaded history, including profile origins");
            using (var frame = fixture.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, $"nonkvp_studies_{evidence}_month_recalc.png"));
            fixture.Close();
        }
        return failures;
    }
}
