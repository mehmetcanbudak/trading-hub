using System.Text.Json;
using TradingHub.App.Services;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;

namespace TradingHub.Tests;

public sealed class IndicatorLibraryTests
{
    private sealed class CustomProbe : Indicator { protected override void OnCalculate(int bar, decimal value) { } }

    private sealed class FutureStudy : Indicator { protected override void OnCalculate(int bar, decimal value) { } }

    private sealed class RegisteredStudy : Indicator { protected override void OnCalculate(int bar, decimal value) { } }

    private static List<IndicatorInfo> Items()
    {
        var catalog = new IndicatorCatalog();
        catalog.Scan(typeof(BigTrades).Assembly, "Trading Hub");
        var items = catalog.Items.ToList();
        items.Add(new IndicatorInfo(typeof(CustomProbe), "Custom probe", "My indicators", UserScripts.Source, ""));
        return items;
    }

    private static string Type<T>() => typeof(T).FullName!;

    [Fact]
    public void Default_arrangement_puts_custom_first_then_groups_in_library_order()
    {
        var items = Items();
        var groups = IndicatorLibrary.Build(items, new IndicatorLibraryLayout());
        Assert.Equal(items.Count, groups.Sum(g => g.Items.Count));
        Assert.Equal(items.Count, groups.SelectMany(g => g.Items).Distinct().Count());
        Assert.Equal(IndicatorLibrary.OwnGroup, groups[0].Id);
        Assert.Equal("★ My indicators", groups[0].Name);
        var known = groups.Select(g => g.Id).Where(id => IndicatorCategories.LibraryOrder.Contains(id)).ToList();
        Assert.Equal(known.OrderBy(id => IndicatorCategories.LibraryOrder.ToList().IndexOf(id)), known);
        Assert.DoesNotContain(groups, g => g.Id is IndicatorCategories.VolumeOrderFlow or IndicatorCategories.Levels or IndicatorCategories.Trend);
        Assert.All(groups, g => Assert.NotEmpty(g.Items));
        Assert.Contains(groups.Single(g => g.Id == IndicatorCategories.Vwap).Items, i => i.Type == typeof(SessionVwap));
        Assert.Contains(groups.Single(g => g.Id == IndicatorCategories.VolumeProfile).Items, i => i.Type == typeof(CompositeVolumeProfile));
        Assert.Contains(groups.Single(g => g.Id == IndicatorCategories.MovingAverages).Items, i => i.Type == typeof(MovingAverage));
    }

    [Fact]
    public void Studies_join_groups_by_category_or_registration()
    {
        Assert.Equal(IndicatorCategories.OrderFlow, IndicatorCategories.LibraryGroup(IndicatorCategories.VolumeOrderFlow));
        Assert.Equal("QuanTAlib", IndicatorCategories.LibraryGroup("QuanTAlib"));
        var byCategory = new IndicatorInfo(typeof(FutureStudy), "Future", IndicatorCategories.Sessions, "Trading Hub", "");
        Assert.Equal(IndicatorCategories.Sessions, IndicatorLibrary.DefaultGroupOf(byCategory));
        var unknown = new IndicatorInfo(typeof(FutureStudy), "Future", "Brand new", "Trading Hub", "");
        var groups = IndicatorLibrary.Build(new[] { unknown, Items()[0] }, new IndicatorLibraryLayout());
        Assert.Equal("Brand new", groups[^1].Id); // unknown categories follow the library order
        var registered = new IndicatorInfo(typeof(RegisteredStudy), "Registered", "Brand new", "Trading Hub", "");
        IndicatorLibrary.RegisterDefaultGroup(Type<RegisteredStudy>(), IndicatorCategories.Macro);
        Assert.Equal(IndicatorCategories.Macro, IndicatorLibrary.DefaultGroupOf(registered));
    }

    [Fact]
    public void Move_reorder_rename_collapse_delete_and_reset_persist_as_json()
    {
        var items = Items();
        var layout = new IndicatorLibraryLayout();
        var flow = IndicatorLibrary.Build(items, layout).First(g => g.Id == IndicatorCategories.OrderFlow);
        var a = flow.Items[0];
        var b = flow.Items[1];

        string id = IndicatorLibrary.CreateGroup(layout, items, "Favorites", flow.Id);
        IndicatorLibrary.Move(layout, items, a.Type.FullName!, id, null);
        var groups = IndicatorLibrary.Build(items, layout);
        int fi = groups.FindIndex(g => g.Id == flow.Id);
        Assert.Equal(id, groups[fi + 1].Id);
        Assert.Equal("Favorites", groups[fi + 1].Name);
        Assert.True(groups[fi + 1].IsUser);
        Assert.Equal(new[] { a }, groups[fi + 1].Items);
        Assert.DoesNotContain(a, groups[fi].Items);

        IndicatorLibrary.Move(layout, items, b.Type.FullName!, id, a.Type.FullName);
        Assert.Equal(new[] { b, a }, IndicatorLibrary.Build(items, layout).First(g => g.Id == id).Items);
        IndicatorLibrary.Shift(layout, items, a.Type.FullName!, -1);
        Assert.Equal(new[] { a, b }, IndicatorLibrary.Build(items, layout).First(g => g.Id == id).Items);
        IndicatorLibrary.Shift(layout, items, a.Type.FullName!, -1); // already first
        Assert.Equal(new[] { a, b }, IndicatorLibrary.Build(items, layout).First(g => g.Id == id).Items);

        IndicatorLibrary.ShiftGroup(layout, items, id, -1);
        groups = IndicatorLibrary.Build(items, layout);
        Assert.Equal(id, groups[groups.FindIndex(g => g.Id == flow.Id) - 1].Id);

        IndicatorLibrary.RenameGroup(layout, IndicatorCategories.Vwap, "My VWAPs");
        IndicatorLibrary.SetCollapsed(layout, IndicatorCategories.Vwap, true);
        var vwap = IndicatorLibrary.Build(items, layout).Single(g => g.Id == IndicatorCategories.Vwap);
        Assert.Equal("My VWAPs", vwap.Name);
        Assert.True(vwap.Collapsed);
        IndicatorLibrary.RenameGroup(layout, IndicatorCategories.Vwap, " ");
        Assert.Equal(IndicatorCategories.Vwap, IndicatorLibrary.Build(items, layout).Single(g => g.Id == IndicatorCategories.Vwap).Name);

        IndicatorLibrary.SortGroup(layout, items, flow.Id);
        var sorted = IndicatorLibrary.Build(items, layout).Single(g => g.Id == flow.Id).Items.Select(i => i.Name).ToList();
        Assert.Equal(sorted.Order(StringComparer.CurrentCultureIgnoreCase), sorted);

        var back = JsonSerializer.Deserialize<IndicatorLibraryLayout>(JsonSerializer.Serialize(layout))!;
        Assert.Equal(IndicatorLibrary.Build(items, layout).Select(g => (g.Id, g.Name, string.Join(",", g.Items.Select(i => i.Type.FullName)))),
            IndicatorLibrary.Build(items, back).Select(g => (g.Id, g.Name, string.Join(",", g.Items.Select(i => i.Type.FullName)))));

        IndicatorLibrary.DeleteGroup(layout, IndicatorCategories.Vwap); // default groups cannot be deleted
        Assert.Contains(IndicatorLibrary.Build(items, layout), g => g.Id == IndicatorCategories.Vwap);
        IndicatorLibrary.DeleteGroup(layout, id);
        groups = IndicatorLibrary.Build(items, layout);
        Assert.DoesNotContain(groups, g => g.Id == id);
        Assert.Contains(a, groups.Single(g => g.Id == flow.Id).Items);
        Assert.Equal(items.Count, groups.Sum(g => g.Items.Count));

        IndicatorLibrary.Reset(layout);
        Assert.Empty(layout.Groups);
    }

    [Fact]
    public void Empty_user_groups_and_placements_of_absent_indicators_survive_other_changes()
    {
        var items = Items();
        var layout = new IndicatorLibraryLayout();
        string empty = IndicatorLibrary.CreateGroup(layout, items, "Later", null);
        string custom = Type<CustomProbe>();
        string keep = IndicatorLibrary.CreateGroup(layout, items, "Mine", null);
        IndicatorLibrary.Move(layout, items, custom, keep, null);

        // the custom indicator is temporarily absent (e.g. its source does not compile)
        var without = items.Where(i => i.Type != typeof(CustomProbe)).ToList();
        IndicatorLibrary.Move(layout, without, Type<Rsi>(), keep, null);
        Assert.Contains(IndicatorLibrary.Build(without, layout), g => g.Id == empty && g.Items.Count == 0);

        var mine = IndicatorLibrary.Build(items, layout).Single(g => g.Id == keep);
        Assert.Equal(new[] { typeof(CustomProbe), typeof(Rsi) }, mine.Items.Select(i => i.Type));
    }

    [Fact]
    public void Existing_settings_without_a_library_layout_still_load()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"ThemeMode":"oscuro"}""")!;
        Assert.NotNull(settings.IndicatorLibrary);
        Assert.Empty(settings.IndicatorLibrary.Groups);
    }

    [System.ComponentModel.DisplayName("Alias probe")]
    public sealed class AliasProbe : Indicator
    {
        public decimal Period { get; set; } = 3;
        protected override void OnCalculate(int bar, decimal value) { }
    }

    [Fact]
    public void A_rename_is_a_display_alias_that_survives_restart_and_never_changes_saved_identity()
    {
        string type = typeof(AliasProbe).FullName!;
        var settings = new AppSettings();
        var onChart = new AliasProbe { Period = 7 };
        try
        {
            IndicatorLibrary.Rename(settings.IndicatorLibrary, type, "  My probe ");
            Assert.Equal("My probe", onChart.Name); // relabels an instance already on a chart
            Assert.Equal("Alias probe", Indicator.DefaultName(typeof(AliasProbe)));
            Assert.Equal("Moving average", new MovingAverage().Name); // other types keep their names
            var info = new IndicatorInfo(typeof(AliasProbe), "Alias probe", IndicatorCategories.Oscillators, "Trading Hub", "");
            Assert.Equal("My probe", IndicatorLibrary.DisplayNameOf(info, settings.IndicatorLibrary));

            // saved workspaces store the type and settings only: no alias text, and they reload into the same type
            var props = Workspace.SaveIndicator(onChart, out var series);
            Assert.DoesNotContain("My probe", props.ToString());
            var reloaded = new AliasProbe();
            Workspace.LoadIndicator(reloaded, props, series);
            Assert.Equal(7m, reloaded.Period);
            Assert.Equal(type, reloaded.GetType().FullName);

            // restart: settings.json round trip, then the start-up Apply
            Indicator.DisplayAlias = null;
            Assert.Equal("Alias probe", new AliasProbe().Name);
            var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
            IndicatorLibrary.Apply(restored.IndicatorLibrary);
            Assert.Equal("My probe", new AliasProbe().Name);

            IndicatorLibrary.Rename(restored.IndicatorLibrary, type, " ");
            Assert.Empty(restored.IndicatorLibrary.Aliases);
            Assert.Equal("Alias probe", onChart.Name);
        }
        finally
        {
            Indicator.DisplayAlias = null;
        }
    }

    [Fact]
    public void Hidden_indicators_leave_the_library_but_still_load_and_come_back_in_place()
    {
        var catalog = new IndicatorCatalog();
        catalog.Scan(typeof(BigTrades).Assembly, "Trading Hub");
        var items = catalog.Items;
        var layout = new IndicatorLibraryLayout();
        string rsi = Type<Rsi>();
        string favorites = IndicatorLibrary.CreateGroup(layout, items, "Favorites", null);
        IndicatorLibrary.Move(layout, items, rsi, favorites, null);

        IndicatorLibrary.Hide(layout, rsi);
        IndicatorLibrary.Hide(layout, rsi);
        var groups = IndicatorLibrary.Build(items, layout);
        Assert.DoesNotContain(groups.SelectMany(g => g.Items), i => i.Type == typeof(Rsi));
        Assert.Equal(items.Count - 1, groups.Sum(g => g.Items.Count));
        Assert.Equal(typeof(Rsi), Assert.Single(IndicatorLibrary.HiddenItems(items, layout)).Type);
        IndicatorLibrary.Shift(layout, items, Type<Macd>(), 1); // other edits keep the hidden placement

        // a saved chart that uses the hidden study still creates and loads it
        var created = Assert.IsType<Rsi>(catalog.Create(rsi));
        Workspace.LoadIndicator(created, Workspace.SaveIndicator(new Rsi(), out var series), series);

        IndicatorLibrary.Unhide(layout, rsi);
        Assert.Empty(IndicatorLibrary.HiddenItems(items, layout));
        Assert.Contains(IndicatorLibrary.Build(items, layout).Single(g => g.Id == favorites).Items, i => i.Type == typeof(Rsi));
        IndicatorLibrary.Hide(layout, rsi);
        IndicatorLibrary.Hide(layout, Type<Macd>());
        IndicatorLibrary.UnhideAll(layout);
        Assert.Empty(layout.Hidden);

        var back = JsonSerializer.Deserialize<IndicatorLibraryLayout>("""{"Groups":[],"Hidden":["X"],"Aliases":{"X":"Y"}}""")!;
        Assert.Equal(new[] { "X" }, back.Hidden);
        Assert.Equal("Y", back.Aliases["X"]);
    }
}
