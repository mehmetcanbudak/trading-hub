using TradingHub.Indicators;

namespace TradingHub.App.Services;

/// <summary>The user's arrangement of the indicator library, stored additively in settings.json.</summary>
public sealed class IndicatorLibraryLayout
{
    /// <summary>Groups in the chosen order. Groups not listed follow at their default position.</summary>
    public List<IndicatorLibraryGroupState> Groups { get; set; } = new();

    /// <summary>Display aliases: indicator full type name → name shown in the library, chart legends, settings and alerts.</summary>
    public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Indicator full type names removed from the library list. They still load and render on saved charts.</summary>
    public List<string> Hidden { get; set; } = new();
}

public sealed class IndicatorLibraryGroupState
{
    /// <summary>A default group id (<see cref="IndicatorCategories.LibraryOrder"/> or another category), <see cref="IndicatorLibrary.OwnGroup"/>, or «user:…».</summary>
    public string Id { get; set; } = "";

    /// <summary>Custom group name; null shows the default (translated) name.</summary>
    public string? Name { get; set; }

    /// <summary>Indicator full type names placed in this group, in order. Others of the group follow by name.</summary>
    public List<string> Items { get; set; } = new();

    public bool Collapsed { get; set; }
}

/// <summary>A library group ready to display.</summary>
public sealed record IndicatorLibraryGroup(string Id, string Name, bool IsUser, bool Collapsed, IReadOnlyList<IndicatorInfo> Items);

/// <summary>
/// The indicator library in groups: custom indicators first, then the default groups in
/// <see cref="IndicatorCategories.LibraryOrder"/>, then any other category; with the user's group order, custom groups,
/// custom group names and item order applied. Saved chart identities never depend on this arrangement.
/// <para>
/// Adding a study to a group: give it <c>[Category(IndicatorCategories.&lt;Group&gt;)]</c>. When its category must stay
/// broader (or cannot be edited), call <see cref="RegisterDefaultGroup"/> before the library is shown, or add it to
/// <see cref="DefaultGroups"/>.
/// </para>
/// </summary>
public static class IndicatorLibrary
{
    public const string OwnGroup = "own";
    public const string UserPrefix = "user:";
    private const string OwnGroupName = "★ My indicators";

    private static readonly Dictionary<string, string> DefaultGroups = new(StringComparer.Ordinal)
    {
        ["TradingHub.Indicators.Native.MovingAverage"] = IndicatorCategories.MovingAverages,
        ["TradingHub.Indicators.Native.QtMovingAverage"] = IndicatorCategories.MovingAverages,
        ["TradingHub.Indicators.Native.QtMaRibbon"] = IndicatorCategories.MovingAverages,
        ["TradingHub.Indicators.Native.Bollinger"] = IndicatorCategories.Volatility,
        ["TradingHub.Indicators.Native.TripleBollinger"] = IndicatorCategories.Volatility,
        ["TradingHub.Indicators.Native.Rsi"] = IndicatorCategories.Oscillators,
        ["TradingHub.Indicators.Native.Macd"] = IndicatorCategories.Oscillators,
        ["TradingHub.Indicators.Native.VolumeBars"] = IndicatorCategories.Volume,
        ["TradingHub.Indicators.Native.RobustVolume"] = IndicatorCategories.Volume,
        ["TradingHub.Indicators.Native.BigTrades"] = IndicatorCategories.Volume,
        ["TradingHub.Indicators.Native.DeltaBars"] = IndicatorCategories.OrderFlow,
        ["TradingHub.Indicators.Native.CumulativeDeltaCandles"] = IndicatorCategories.OrderFlow,
        ["TradingHub.Indicators.Native.DeltaBubbles"] = IndicatorCategories.OrderFlow,
        ["TradingHub.Indicators.Native.RefillZones"] = IndicatorCategories.OrderFlow,
        ["TradingHub.Indicators.Native.CompositeVolumeProfile"] = IndicatorCategories.VolumeProfile,
        ["TradingHub.Indicators.Native.SessionVolumeProfile"] = IndicatorCategories.VolumeProfile,
        ["TradingHub.Indicators.Native.NakedPoc"] = IndicatorCategories.VolumeProfile,
        ["TradingHub.Indicators.Native.SessionVwap"] = IndicatorCategories.Vwap,
        ["TradingHub.Indicators.Native.AnchoredFlowVwap"] = IndicatorCategories.Vwap,
        ["TradingHub.Indicators.Native.VwapClaire"] = IndicatorCategories.Vwap,
        ["TradingHub.Indicators.Native.MultiPeriodVwap"] = IndicatorCategories.Vwap,
        ["TradingHub.Indicators.Native.PriorClosingVwap"] = IndicatorCategories.Vwap,
        ["TradingHub.Indicators.Native.RollingVwap"] = IndicatorCategories.Vwap,
        ["TradingHub.Indicators.Native.TimedSessionVwap"] = IndicatorCategories.Vwap,
        ["TradingHub.Indicators.Native.OrderbookDelta"] = IndicatorCategories.OrderBook,
        ["TradingHub.Indicators.Native.OpenInterest"] = IndicatorCategories.Derivatives,
        ["TradingHub.Indicators.Native.HtfStructure"] = IndicatorCategories.Structure,
        ["TradingHub.Indicators.Native.MarketStructure"] = IndicatorCategories.Structure,
        ["TradingHub.Indicators.Native.KeyLevels"] = IndicatorCategories.Structure,
        ["TradingHub.Indicators.Native.QtCamarilla"] = IndicatorCategories.Structure,
        ["TradingHub.Indicators.Native.DoubleInsideDay"] = IndicatorCategories.Patterns,
        ["TradingHub.Indicators.Native.SessionBackgrounds"] = IndicatorCategories.Sessions,
    };

    /// <summary>Assigns a built-in type's default group (call once at start-up, before the library is shown).</summary>
    public static void RegisterDefaultGroup(string typeFullName, string group)
    {
        lock (DefaultGroups) DefaultGroups[typeFullName] = group;
    }

    /// <summary>Custom indicators (including editable copies) are listed together; built-ins by table or category.</summary>
    public static string DefaultGroupOf(IndicatorInfo info)
    {
        if (info.Source == UserScripts.Source) return OwnGroup;
        lock (DefaultGroups)
            if (DefaultGroups.TryGetValue(info.Type.FullName!, out var group)) return group;
        return IndicatorCategories.LibraryGroup(info.Category);
    }

    public static string DefaultGroupName(string id) => id == OwnGroup ? OwnGroupName : id;

    /// <summary>Default rank: custom first, then the library order, then other categories alphabetically.</summary>
    private static (int, string) DefaultRank(string id)
    {
        if (id == OwnGroup) return (-1, "");
        int k = IndicatorCategories.LibraryOrder.ToList().IndexOf(id);
        return k >= 0 ? (k, "") : (IndicatorCategories.LibraryOrder.Count, id);
    }

    public static List<IndicatorLibraryGroup> Build(IReadOnlyList<IndicatorInfo> items, IndicatorLibraryLayout layout)
    {
        var hidden = layout.Hidden.ToHashSet(StringComparer.Ordinal);
        var byType = new Dictionary<string, IndicatorInfo>(StringComparer.Ordinal);
        foreach (var info in items)
            if (!hidden.Contains(info.Type.FullName!)) byType.TryAdd(info.Type.FullName!, info);

        // where the user placed each indicator (first placement wins)
        var placed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in layout.Groups)
            foreach (var t in g.Items)
                if (byType.ContainsKey(t)) placed.TryAdd(t, g.Id);

        var groups = new Dictionary<string, List<IndicatorInfo>>(StringComparer.Ordinal);
        List<IndicatorInfo> ListOf(string id) => groups.TryGetValue(id, out var list) ? list : groups[id] = new List<IndicatorInfo>();
        foreach (var g in layout.Groups)
        {
            var list = ListOf(g.Id);
            foreach (var t in g.Items)
                if (byType.TryGetValue(t, out var info) && placed[t] == g.Id && !list.Contains(info)) list.Add(info);
        }

        // everything else in its default group, after hand-ordered items, by name
        foreach (var info in byType.Values.Where(i => !placed.ContainsKey(i.Type.FullName!)).OrderBy(i => DisplayNameOf(i, layout), StringComparer.CurrentCultureIgnoreCase))
            ListOf(DefaultGroupOf(info)).Add(info);

        // group order: the user's, then unlisted groups at their default rank (before the first later default group)
        var order = new List<string>();
        foreach (var g in layout.Groups)
            if (!order.Contains(g.Id)) order.Add(g.Id);
        foreach (var id in groups.Keys.Where(k => !order.Contains(k)).OrderBy(DefaultRank))
        {
            var rank = DefaultRank(id);
            int at = order.FindIndex(o => !o.StartsWith(UserPrefix, StringComparison.Ordinal) && DefaultRank(o).CompareTo(rank) > 0);
            if (at < 0) order.Add(id);
            else order.Insert(at, id);
        }

        var result = new List<IndicatorLibraryGroup>();
        foreach (var id in order)
        {
            var state = layout.Groups.FirstOrDefault(g => g.Id == id);
            bool user = id.StartsWith(UserPrefix, StringComparison.Ordinal);
            var list = groups.TryGetValue(id, out var l) ? l : new List<IndicatorInfo>();
            if (list.Count == 0 && !user) continue; // an empty default group is not shown
            result.Add(new IndicatorLibraryGroup(id, state?.Name is { Length: > 0 } name ? name : DefaultGroupName(id), user, state?.Collapsed ?? false, list));
        }

        return result;
    }

    /// <summary>Copies the displayed arrangement into the layout so one move never reorders anything else.</summary>
    private static void Materialize(IndicatorLibraryLayout layout, IReadOnlyList<IndicatorLibraryGroup> shown)
    {
        var old = layout.Groups.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        layout.Groups = shown.Select(g => new IndicatorLibraryGroupState
        {
            Id = g.Id,
            Name = old.TryGetValue(g.Id, out var o) ? o.Name : null,
            Collapsed = g.Collapsed,
            Items = g.Items.Select(i => i.Type.FullName!).ToList(),
        }).ToList();
        // keep custom names, collapsed state and placements of groups currently empty or of indicators not loaded now
        foreach (var o in old.Values.Where(o => layout.Groups.All(g => g.Id != o.Id)))
            if (o.Id.StartsWith(UserPrefix, StringComparison.Ordinal) || o.Name != null || o.Items.Count > 0) layout.Groups.Add(o);
        foreach (var g in layout.Groups)
            if (old.TryGetValue(g.Id, out var previous))
                foreach (var missing in previous.Items.Where(t => !shown.Any(s => s.Items.Any(i => i.Type.FullName == t)) && !g.Items.Contains(t)))
                    g.Items.Add(missing); // an indicator absent today (e.g. a custom one not compiled yet) keeps its place
    }

    private static IndicatorLibraryGroupState Group(IndicatorLibraryLayout layout, string id)
    {
        var g = layout.Groups.FirstOrDefault(x => x.Id == id);
        if (g != null) return g;
        g = new IndicatorLibraryGroupState { Id = id };
        layout.Groups.Add(g);
        return g;
    }

    /// <summary>Moves an indicator into <paramref name="toGroup"/>, before <paramref name="beforeType"/> (null = last).</summary>
    public static void Move(IndicatorLibraryLayout layout, IReadOnlyList<IndicatorInfo> items, string type, string toGroup, string? beforeType)
    {
        Materialize(layout, Build(items, layout));
        foreach (var g in layout.Groups) g.Items.Remove(type);
        var target = Group(layout, toGroup);
        int at = beforeType == null ? -1 : target.Items.IndexOf(beforeType);
        if (at < 0) target.Items.Add(type);
        else target.Items.Insert(at, type);
    }

    /// <summary>Moves an indicator up (−1) or down (+1) within its group.</summary>
    public static void Shift(IndicatorLibraryLayout layout, IReadOnlyList<IndicatorInfo> items, string type, int delta)
    {
        var shown = Build(items, layout);
        Materialize(layout, shown);
        var g = layout.Groups.FirstOrDefault(x => x.Items.Contains(type));
        if (g == null) return;
        // move past visible neighbours only (an entry for an indicator absent today does not absorb a step)
        var visible = shown.First(s => s.Id == g.Id).Items.Select(i => i.Type.FullName!).ToList();
        int i = visible.IndexOf(type), j = Math.Clamp(i + delta, 0, visible.Count - 1);
        if (i < 0 || i == j) return;
        g.Items.Remove(type);
        g.Items.Insert(g.Items.IndexOf(visible[j]) + (delta > 0 ? 1 : 0), type);
    }

    /// <summary>Moves a group up (−1) or down (+1).</summary>
    public static void ShiftGroup(IndicatorLibraryLayout layout, IReadOnlyList<IndicatorInfo> items, string id, int delta)
    {
        var shown = Build(items, layout);
        Materialize(layout, shown);
        var visible = shown.Select(g => g.Id).ToList();
        int i = visible.IndexOf(id), j = Math.Clamp(i + delta, 0, visible.Count - 1);
        if (i < 0 || i == j) return;
        var group = layout.Groups.First(g => g.Id == id);
        layout.Groups.Remove(group);
        layout.Groups.Insert(layout.Groups.FindIndex(g => g.Id == visible[j]) + (delta > 0 ? 1 : 0), group);
    }

    /// <summary>A new, empty user group after <paramref name="afterId"/> (null = last). Returns its id.</summary>
    public static string CreateGroup(IndicatorLibraryLayout layout, IReadOnlyList<IndicatorInfo> items, string name, string? afterId)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a group name.", nameof(name));
        Materialize(layout, Build(items, layout));
        var g = new IndicatorLibraryGroupState { Id = UserPrefix + Guid.NewGuid().ToString("N")[..8], Name = name.Trim() };
        int at = afterId == null ? -1 : layout.Groups.FindIndex(x => x.Id == afterId);
        if (at < 0) layout.Groups.Add(g);
        else layout.Groups.Insert(at + 1, g);
        return g.Id;
    }

    /// <summary>Sets a custom group name; empty restores the default name (user groups keep their last name).</summary>
    public static void RenameGroup(IndicatorLibraryLayout layout, string id, string? name)
    {
        var g = Group(layout, id);
        if (!string.IsNullOrWhiteSpace(name)) g.Name = name.Trim();
        else if (!id.StartsWith(UserPrefix, StringComparison.Ordinal)) g.Name = null;
    }

    /// <summary>Deletes a user group; its indicators return to their default groups.</summary>
    public static void DeleteGroup(IndicatorLibraryLayout layout, string id)
    {
        if (!id.StartsWith(UserPrefix, StringComparison.Ordinal)) return;
        layout.Groups.RemoveAll(g => g.Id == id);
    }

    public static void SetCollapsed(IndicatorLibraryLayout layout, string id, bool collapsed) => Group(layout, id).Collapsed = collapsed;

    /// <summary>Sorts a group by name (A → Z).</summary>
    public static void SortGroup(IndicatorLibraryLayout layout, IReadOnlyList<IndicatorInfo> items, string id)
    {
        Materialize(layout, Build(items, layout));
        var g = Group(layout, id);
        var names = items.GroupBy(i => i.Type.FullName!).ToDictionary(x => x.Key, x => DisplayNameOf(x.First(), layout), StringComparer.Ordinal);
        g.Items = g.Items.OrderBy(t => names.TryGetValue(t, out var n) ? n : t, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Back to the default arrangement (groups, order and group names). Aliases and hidden indicators have their own restore actions.</summary>
    public static void Reset(IndicatorLibraryLayout layout) => layout.Groups.Clear();

    // ------------------------------------------------------------------ display aliases and hidden indicators

    /// <summary>The library's name for an indicator: its alias, else the catalog (factory) name.</summary>
    public static string DisplayNameOf(IndicatorInfo info, IndicatorLibraryLayout layout) =>
        layout.Aliases.TryGetValue(info.Type.FullName!, out var alias) && !string.IsNullOrWhiteSpace(alias) ? alias : info.Name;

    /// <summary>
    /// Installs the layout's aliases as the SDK display-name resolver (an immutable snapshot, safe from any thread).
    /// Call at start-up and after every rename.
    /// </summary>
    public static void Apply(IndicatorLibraryLayout layout)
    {
        var snapshot = new Dictionary<string, string>(layout.Aliases.Where(p => !string.IsNullOrWhiteSpace(p.Value)), StringComparer.Ordinal);
        Indicator.DisplayAlias = snapshot.Count == 0 ? null : type => type.FullName is { } name && snapshot.TryGetValue(name, out var alias) ? alias : null;
    }

    /// <summary>Sets (or with null/blank removes) the display alias of a type. Type and saved identities are unchanged.</summary>
    public static void Rename(IndicatorLibraryLayout layout, string type, string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) layout.Aliases.Remove(type);
        else layout.Aliases[type] = alias.Trim();
        Apply(layout);
    }

    /// <summary>Removes an indicator from the library list only; charts that use it keep loading it.</summary>
    public static void Hide(IndicatorLibraryLayout layout, string type)
    {
        if (!layout.Hidden.Contains(type)) layout.Hidden.Add(type);
    }

    public static void Unhide(IndicatorLibraryLayout layout, string type) => layout.Hidden.Remove(type);

    public static void UnhideAll(IndicatorLibraryLayout layout) => layout.Hidden.Clear();

    /// <summary>Hidden indicators that are currently loaded, by display name.</summary>
    public static List<IndicatorInfo> HiddenItems(IReadOnlyList<IndicatorInfo> items, IndicatorLibraryLayout layout)
    {
        var hidden = layout.Hidden.ToHashSet(StringComparer.Ordinal);
        return items.Where(i => hidden.Contains(i.Type.FullName!)).DistinctBy(i => i.Type.FullName)
            .OrderBy(i => DisplayNameOf(i, layout), StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
