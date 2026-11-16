using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
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
/// The unified volume profile and VWAP suite through the real catalog, settings window, chart renderer, right-click merge
/// actions and workspace persistence. Isolated simulated prints only; repeat with TRADINGHUB_LANG=es and zh-CN.
/// </summary>
internal static class ProfilesVwapScene
{
    public static int Run(MainWindow owner, Action<double> pump, string outDir)
    {
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        int checks = 0, failures = 0;
        void Check(bool value, string description)
        {
            checks++;
            Console.WriteLine($"{(value ? "OK" : "FAIL")} Profiles/VWAP: {description}");
            if (!value) failures++;
        }
        string language = Lang.Current;
        var instrument = new Instrument { FeedId = "profiles-vwap-ui", Symbol = "PROFILE-VWAP-FIXTURE", TickSize = 0.25m, VolumeStep = 1 };
        using var feed = new SimulatedFeed();
        using var data = new InstrumentData(instrument, feed);
        var start = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);
        long id = 0;
        for (int bar = 0; bar < 6 * 48; bar++)
        {
            decimal center = 100 + bar / 48 * 2 + (decimal)Math.Round(6 * Math.Sin(bar / 7.0), 2);
            foreach (var (offset, volume, side) in new[] { (-0.5m, 3m, TradeSide.Sell), (0m, 2m, TradeSide.Buy), (0.75m, 5m, TradeSide.Buy) })
                data.Trades.Add(new Trade(++id, start.AddMinutes(bar * 30).AddSeconds(id % 20), center + offset, volume, side));
        }
        using var model = new ChartModel(data, ChartPeriod.M30, new ChartSettings { TimeZoneId = "UTC" });
        var view = new ChartView { Model = model };
        var fixture = new Window { Title = "Volume profile and VWAP suite — isolated fixture", Width = 1380, Height = 900, Content = view };
        fixture.Show(owner); pump(0.15);
        try
        {
            var profile = new VolumeProfile { ShowLabels = true };
            var suite = new VwapSuite { UseMultiPeriod = true, UseRolling = true, UsePriorClosing = true };
            foreach (var type in new[] { typeof(VolumeProfile), typeof(VwapSuite), typeof(CompositeVolumeProfile), typeof(SessionVolumeProfile), typeof(SessionVwap), typeof(VwapClaire) })
                Check(App.Services.Catalog.Find(type.FullName!)?.Type == type, "the real catalog exposes " + type.Name + " (saved charts keep loading)");
            var profileSlot = model.AddIndicator(profile, applyDefaultColors: false);
            var suiteSlot = model.AddIndicator(suite, applyDefaultColors: false);
            model.Update(); view.InvalidateVisual(); pump(0.15);
            Check(profileSlot.Error == null && suiteSlot.Error == null, "both studies calculate through the actual ChartModel: " + profileSlot.Error + suiteSlot.Error);
            Check(profile.Profiles.Count == 6, "six session profiles before merging");

            // Settings: unified-profile options and a nested VWAP part are real editors.
            var settings = new IndicatorsWindow(App.Services, model);
            settings.Show(fixture); pump(0.15);
            try
            {
                Grid? Row(IndicatorSlot slot, string label)
                {
                    var list = settings.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(l => l.Items.OfType<ListBoxItem>().Any(i => ReferenceEquals(i.Tag, slot)));
                    if (list == null) return null;
                    list.SelectedItem = list.Items.OfType<ListBoxItem>().Single(i => ReferenceEquals(i.Tag, slot)); pump(0.08);
                    foreach (var expander in settings.GetVisualDescendants().OfType<Expander>()) expander.IsExpanded = true;
                    pump(0.08);
                    return settings.GetVisualDescendants().OfType<PropertyGrid>().Single().GetVisualDescendants().OfType<Grid>()
                        .FirstOrDefault(g => g.Children.OfType<TextBlock>().Any(t => t.Text == label));
                }
                string Label(PropertyInfo property) => Lang.T(PropertyGrid.NameOf(property));
                foreach (var (name, english) in new[] { (nameof(CompositeVolumeProfile.View), "Profile view"), (nameof(CompositeVolumeProfile.MergedPeriods), "Manual merges (UTC period start..end; separated by ;)"),
                    (nameof(CompositeVolumeProfile.TicksPerRow), "Ticks per row"), (nameof(CompositeVolumeProfile.MergeRule), "Automatic merge rule") })
                {
                    var property = typeof(VolumeProfile).GetProperty(name)!;
                    if (language != Lang.English) Check(Label(property) != english, $"{name} label is translated in {language}: {Label(property)}");
                    Check(Row(profileSlot, Label(property)) != null, "Volume profile exposes " + name);
                }
                var viewRow = Row(profileSlot, Label(typeof(VolumeProfile).GetProperty(nameof(CompositeVolumeProfile.View))!));
                if (viewRow?.Children.OfType<ComboBox>().FirstOrDefault() is { } combo)
                {
                    combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, CompositeVolumeProfile.ProfileView.Delta));
                    model.Update(); pump(0.05);
                    Check(profile.View == CompositeVolumeProfile.ProfileView.Delta, "the view selector switches the real profile to delta");
                }
                else Check(false, "profile view selector is a combo box");
                using (var frame = settings.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, $"profiles_vwap_settings_profile_{language}.png"));

                var length = typeof(RollingVwap).GetProperty(nameof(RollingVwap.Length))!;
                var lengthRow = Row(suiteSlot, Label(length));
                Check(lengthRow != null, "VWAP suite exposes the nested rolling-window length");
                if (lengthRow?.Children.OfType<NumericUpDown>().FirstOrDefault() is { } number)
                {
                    number.Value = 90; model.Update(); pump(0.05);
                    Check(suite.Rolling.Length == 90 && suiteSlot.Error == null, "the nested editor changes the real rolling part");
                }
                var useClaire = typeof(VwapSuite).GetProperty(nameof(VwapSuite.UseClaire))!;
                if (language != Lang.English) Check(Label(useClaire) != PropertyGrid.NameOf(useClaire), $"VWAP part toggle is translated in {language}");
                if (Row(suiteSlot, Label(useClaire))?.Children.OfType<CheckBox>().FirstOrDefault() is { } claire)
                {
                    claire.IsChecked = true; model.Update(); pump(0.05);
                    var r30 = (OptionalValueDataSeries)suite.DataSeries.Single(s => s.Id == "Claire.R30");
                    Check(suite.UseClaire && !r30.IsHidden && suiteSlot.Error == null, "switching a part on calculates it inside the suite");
                }
                else Check(false, "VWAP part toggle is a checkbox");
                using (var frame = settings.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, $"profiles_vwap_settings_suite_{language}.png"));
            }
            finally { settings.Close(); }

            // Head-to-tail merge through the indicator's right-click actions at real chart coordinates.
            profile.View = CompositeVolumeProfile.ProfileView.Volume;
            model.Settings.Style = ChartStyle.Candles;
            model.Viewport.BarWidth = 4;
            model.Viewport.AutoScale = true;
            model.Update(); view.InvalidateVisual(); pump(0.15);
            int X(int bar) => model.ChartInfo.GetXByBar(bar);
            var firstTarget = profile.Profiles[1];
            var markAt = profile.GetChartMenuItems(X((firstTarget.StartBar + firstTarget.EndBar) / 2), 100);
            var mark = markAt.FirstOrDefault(i => i.Text == "Profiles: mark this profile as the merge start");
            Check(mark != null, "right-click on a profile offers marking the merge start");
            mark?.Run();
            var lastTarget = profile.Profiles[3];
            var mergeAt = profile.GetChartMenuItems(X((lastTarget.StartBar + lastTarget.EndBar) / 2), 100);
            var merge = mergeAt.FirstOrDefault(i => i.Text == "Profiles: merge from the marked start to this profile");
            Check(merge != null, "right-click on a later profile offers merging to it");
            decimal total = profile.Profiles.Sum(p => p.Total);
            merge?.Run();
            model.Update(); view.InvalidateVisual(); pump(0.15);
            var merged = profile.Profiles.FirstOrDefault(p => p.Parts == 3);
            Check(profile.Profiles.Count == 4 && merged?.Merge == CompositeVolumeProfile.MergeKind.Manual && profile.Profiles.Sum(p => p.Total) == total,
                "three sessions become one manual profile and volume is conserved");
            Check(App.Services != null && Workspace.SaveIndicator(profile, out _)["MergedPeriods"]?.ToString() == profile.MergedPeriods && profile.MergedPeriods.Length > 0,
                "the merge is saved with the chart's indicator settings");
            using (var frame = fixture.CaptureRenderedFrame())
            {
                Check(frame != null, "merged profile and VWAP suite render in an actual chart frame");
                frame?.Save(Path.Combine(outDir, $"profiles_vwap_chart_merged_{language}.png"));
            }
            var split = profile.GetChartMenuItems(X((merged!.StartBar + merged.EndBar) / 2), 100).FirstOrDefault(i => i.Text == "Profiles: split this merged profile");
            Check(split != null, "a merged profile offers splitting");
            split?.Run(); model.Update();
            Check(profile.Profiles.Count == 6 && profile.MergedPeriods.Length == 0, "splitting restores the six source profiles");
        }
        finally { fixture.Close(); }
        File.WriteAllText(Path.Combine(outDir, $"profiles-vwap-results-{language}.json"), System.Text.Json.JsonSerializer.Serialize(new { checks, failures, language }));
        return failures;
    }
}
