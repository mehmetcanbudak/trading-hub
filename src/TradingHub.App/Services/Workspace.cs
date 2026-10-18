using System.ComponentModel;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using TradingHub.App.Views;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;
using TradingHub.Common.Logging;
using TradingHub.Data;
using TradingHub.Indicators;

namespace TradingHub.App.Services;

/// <summary>Guarda y restaura el espacio de trabajo (pestañas, indicadores con su configuración y dibujos).</summary>
internal static partial class Workspace
{
    private sealed class WorkspaceDto
    {
        public int Version { get; set; } = 2;
        public List<PageDto> Pages { get; set; } = new();
        public int ActivePage { get; set; }
        public List<FloatingDto> Floating { get; set; } = new();

        // versión 1: una pestaña = un gráfico
        public List<TabDto> Tabs { get; set; } = new();
        public int Active { get; set; }
    }

    private sealed class PageDto
    {
        public PaneLayout Layout { get; set; } = PaneLayout.One;
        public int Active { get; set; }
        public List<TabDto> Charts { get; set; } = new();
    }

    private sealed class FloatingDto
    {
        public TabDto Chart { get; set; } = new();
        public int X { get; set; }
        public int Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    private sealed class TabDto
    {
        public string FeedId { get; set; } = "";
        public string Symbol { get; set; } = "";
        public string Period { get; set; } = "M5";
        public JObject? Settings { get; set; }
        public double BarWidth { get; set; }
        public string AccountId { get; set; } = "sim";
        public double HistoryHours { get; set; }
        public int LinkGroup { get; set; }
        public decimal Quantity { get; set; }
        public string? CrossSymbol { get; set; }
        public bool TraderVisible { get; set; } = true;
        public List<IndicatorDto> Indicators { get; set; } = new();
        public List<StrategyDto> Strategies { get; set; } = new();
    }

    /// <summary>Una estrategia puesta en un gráfico.</summary>
    private sealed class StrategyDto
    {
        public string Id { get; set; } = "";
        public JObject Parameters { get; set; } = new();
        public bool Live { get; set; }
        public string AccountId { get; set; } = "sim";
        public bool Visible { get; set; } = true;
    }

    private sealed class IndicatorDto
    {
        public string Type { get; set; } = "";
        public bool Visible { get; set; } = true;
        public JObject Properties { get; set; } = new();
        public List<JObject> Series { get; set; } = new();
        public double? PanelHeight { get; set; }
    }

    private static readonly JsonSerializerSettings Settings = new()
    {
        ContractResolver = new EditableContractResolver(),
        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
        NullValueHandling = NullValueHandling.Ignore,
        ObjectCreationHandling = ObjectCreationHandling.Replace,
        Error = (_, e) => e.ErrorContext.Handled = true,
    };

    private static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);

    /// <summary>Solo serializa propiedades editables (públicas, con set, visibles en el editor).</summary>
    private sealed class EditableContractResolver : DefaultContractResolver
    {
        protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization)
        {
            var props = base.CreateProperties(type, memberSerialization);
            return props.Where(p =>
            {
                if (!p.Writable || !p.Readable) return false;
                var pi = type.GetProperty(p.UnderlyingName!, BindingFlags.Public | BindingFlags.Instance);
                if (pi == null || pi.GetIndexParameters().Length > 0) return false;
                if (pi.GetCustomAttribute<BrowsableAttribute>() is { Browsable: false }) return false;
                if (typeof(Indicator).IsAssignableFrom(type) && pi.DeclaringType == typeof(Indicator) && pi.Name != nameof(Indicator.IsVisible)) return false;
                return true;
            }).ToList();
        }
    }

    // ===================================================================== indicadores

    public static JObject SaveIndicator(Indicator ind, out List<JObject> series)
    {
        var props = JObject.FromObject(ind, Serializer);
        series = new List<JObject>();
        foreach (var s in ind.DataSeries)
        {
            var o = new JObject { ["Id"] = s.Id, ["Visible"] = s.Visible };
            switch (s)
            {
                case ValueDataSeries v:
                    o["Color"] = v.Color.ToArgb();
                    o["Width"] = v.Width;
                    o["VisualType"] = v.VisualType.ToString();
                    o["LineDashStyle"] = v.LineDashStyle.ToString();
                    break;
                case RangeDataSeries r:
                    o["RangeColor"] = r.RangeColor.ToArgb();
                    break;
                case CandleDataSeries c:
                    o["Up"] = c.UpCandleColor.ToArgb();
                    o["Down"] = c.DownCandleColor.ToArgb();
                    break;
            }

            series.Add(o);
        }

        return props;
    }

    public static void LoadIndicator(Indicator ind, JObject props, List<JObject>? series)
    {
        try
        {
            using var reader = props.CreateReader();
            Serializer.Populate(reader, ind);
        }
        catch (Exception ex)
        {
            ind.LogWarn($"Could not restore all settings for {ind.Name}: {ex.Message}");
        }

        if (series == null) return;
        foreach (var o in series)
        {
            var s = ind.DataSeries.FirstOrDefault(x => x.Id == (string?)o["Id"]);
            if (s == null) continue;
            if (o["Visible"] is JValue vis) s.Visible = (bool)vis;
            switch (s)
            {
                case ValueDataSeries v:
                    if (o["Color"] is JValue c) v.Color = System.Drawing.Color.FromArgb((int)c);
                    if (o["Width"] is JValue w) v.Width = (int)w;
                    if (Enum.TryParse<VisualMode>((string?)o["VisualType"], out var vm)) v.VisualType = vm;
                    if (Enum.TryParse<LineDashStyle>((string?)o["LineDashStyle"], out var ds)) v.LineDashStyle = ds;
                    break;
                case RangeDataSeries r when o["RangeColor"] is JValue rc:
                    r.RangeColor = System.Drawing.Color.FromArgb((int)rc);
                    break;
                case CandleDataSeries cd:
                    if (o["Up"] is JValue up) cd.UpCandleColor = System.Drawing.Color.FromArgb((int)up);
                    if (o["Down"] is JValue dn) cd.DownCandleColor = System.Drawing.Color.FromArgb((int)dn);
                    break;
            }
        }
    }

    /// <summary>Crea una copia de un indicador con la misma configuración.</summary>
    public static Indicator? CloneIndicator(AppServices svc, Indicator ind)
    {
        try
        {
            var copy = (Indicator?)Activator.CreateInstance(ind.GetType());
            if (copy == null) return null;
            var props = SaveIndicator(ind, out var series);
            LoadIndicator(copy, props, series);
            return copy;
        }
        catch (Exception ex)
        {
            ind.LogWarn($"Could not copy {ind.Name}: {ex.Message}");
            return null;
        }
    }

    // ===================================================================== espacio de trabajo

    private static TabDto ToDto(ChartTab t)
    {
        var td = new TabDto
        {
            FeedId = t.FeedId,
            Symbol = t.Symbol,
            Period = t.Model!.Period.Code,
            BarWidth = t.Model.Viewport.BarWidth,
            AccountId = t.AccountId,
            HistoryHours = t.HistoryHours,
            LinkGroup = t.LinkGroup,
            Quantity = t.Quantity,
            CrossSymbol = t.CrossSymbol,
            TraderVisible = t.TraderVisible,
            Settings = JObject.FromObject(t.Model.Settings, Serializer),
        };
        foreach (var slot in t.Model.Indicators) td.Indicators.Add(ToIndicatorDto(slot));
        foreach (var cs in t.Strategies)
            td.Strategies.Add(new StrategyDto
            {
                Id = cs.StrategyId,
                Parameters = JObject.FromObject(cs.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value is System.Text.Json.JsonElement je ? (object?)je.ToString() : kv.Value)),
                Live = cs.Live,
                AccountId = cs.AccountId,
                Visible = cs.Overlay.Visible,
            });
        return td;
    }

    private static IndicatorDto ToIndicatorDto(IndicatorSlot slot)
    {
        var props = SaveIndicator(slot.Indicator, out var series);
        return new IndicatorDto
        {
            Type = slot.Indicator.GetType().FullName!,
            Visible = slot.Visible,
            Properties = props,
            Series = series,
            PanelHeight = slot.Panel?.HeightRatio,
        };
    }

    /// <summary>Agrega al gráfico los indicadores guardados (del espacio de trabajo o de una plantilla).</summary>
    private static void AddIndicators(TradingView w, ChartModel model, IEnumerable<IndicatorDto> list)
    {
        foreach (var idto in list)
        {
            var ind = w.Services.Catalog.Create(idto.Type);
            if (ind == null)
            {
                w.SetStatus($"Indicator not found: {idto.Type} (deleted or failing to compile?)");
                continue;
            }

            LoadIndicator(ind, idto.Properties, idto.Series);
            var slot = model.AddIndicator(ind, applyDefaultColors: false);
            slot.Visible = idto.Visible;
            if (slot.Panel != null && idto.PanelHeight is double h) slot.Panel.HeightRatio = h;
        }
    }

    private static bool Saveable(ChartTab t) => t.Model != null && !t.IsReplay;

    public static void Save(TradingView w)
    {
        try
        {
            var dto = new WorkspaceDto();
            foreach (var page in w.Pages)
            {
                var charts = page.Charts.Where(Saveable).ToList();
                if (charts.Count == 0) continue;
                var pd = new PageDto { Layout = page.Layout, Active = Math.Max(0, charts.IndexOf(page.Active!)) };
                pd.Charts.AddRange(charts.Select(ToDto));
                if (ReferenceEquals(page, w.ActivePage)) dto.ActivePage = dto.Pages.Count;
                dto.Pages.Add(pd);
            }

            foreach (var fw in w.FloatingWindows.Where(f => Saveable(f.Chart)))
            {
                dto.Floating.Add(new FloatingDto
                {
                    Chart = ToDto(fw.Chart),
                    X = fw.Position.X,
                    Y = fw.Position.Y,
                    Width = fw.Width,
                    Height = fw.Height,
                });
            }

            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.WorkspaceFile, JsonConvert.SerializeObject(dto, Formatting.Indented, Settings));
            foreach (var t in w.Tabs.Where(Saveable)) SaveDrawings(t);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    private static ChartSettings SettingsOf(TabDto td)
    {
        var settings = new ChartSettings();
        if (td.Settings != null)
        {
            using var r = td.Settings.CreateReader();
            Serializer.Populate(r, settings);
        }

        if (settings.Layout.Columns.Count == 0) settings.Layout = FootprintLayout.Presets[0].Clone();
        return settings;
    }

    private static void Finish(TradingView w, ChartTab tab, TabDto td)
    {
        tab.LinkGroup = td.LinkGroup;
        tab.Quantity = td.Quantity;
        tab.TraderVisible = td.TraderVisible;
        tab.Trader?.ApplyVisible();
        tab.Trader?.Rebind();
        if (!string.IsNullOrWhiteSpace(td.CrossSymbol)) _ = w.SetCrossTradingAsync(tab, td.CrossSymbol, save: false);
        if (tab.Model == null) return;
        if (td.BarWidth > 0) tab.Model.Viewport.BarWidth = td.BarWidth;
        AddIndicators(w, tab.Model, td.Indicators);

        // estrategias: las visuales vuelven a correr; las que ejecutaban quedan pausadas hasta que las reanudes
        foreach (var sd in td.Strategies)
        {
            var cs = new ChartStrategy
            {
                StrategyId = sd.Id,
                Parameters = sd.Parameters.Properties().ToDictionary(p => p.Name, p => (object?)p.Value.ToObject<object>()),
                Live = sd.Live,
                AccountId = sd.AccountId,
                Paused = sd.Live,
            };
            cs.Overlay.Visible = sd.Visible;
            cs.Overlay.Tag = cs;
            cs.Overlay.Name = sd.Id.Split('.').Last();
            tab.Strategies.Add(cs);
        }

        if (tab.Strategies.Count > 0) w.RestartStrategies(tab, instrumentChanged: false);
    }

    private static ChartPeriod PeriodOf(TabDto td) => ChartPeriod.TryParse(td.Period, out var p) ? p : ChartPeriod.M5;

    public static async Task<bool> RestoreAsync(TradingView w)
    {
        if (!File.Exists(AppPaths.WorkspaceFile)) return false;
        var dto = JsonConvert.DeserializeObject<WorkspaceDto>(File.ReadAllText(AppPaths.WorkspaceFile), Settings);
        if (dto == null) return false;
        var pages = dto.Pages;
        int activePage = dto.ActivePage;
        if (pages.Count == 0 && dto.Tabs.Count > 0)
        {
            // espacio de trabajo viejo: cada pestaña tenía un solo gráfico
            pages = dto.Tabs.Select(t => new PageDto { Charts = { t } }).ToList();
            activePage = dto.Active;
        }

        if (pages.All(p => p.Charts.Count == 0)) return false;

        ChartTab? activate = null;
        for (int i = 0; i < pages.Count; i++)
        {
            var pd = pages[i];
            if (pd.Charts.Count == 0) continue;
            var first = pd.Charts[0];
            var tab = await w.NewTabAsync(first.FeedId, first.Symbol, PeriodOf(first), SettingsOf(first), activate: i == activePage || i == 0,
                accountId: first.AccountId, historyHours: first.HistoryHours, layout: pd.Layout);
            Finish(w, tab, first);
            var page = tab.Page!;
            var charts = new List<ChartTab> { tab };
            foreach (var td in pd.Charts.Skip(1))
            {
                var t = await w.AddToPageAsync(page, td.FeedId, td.Symbol, PeriodOf(td), SettingsOf(td), td.AccountId, td.HistoryHours);
                Finish(w, t, td);
                charts.Add(t);
            }

            page.Active = charts[Math.Clamp(pd.Active, 0, charts.Count - 1)];
            if (i == activePage) activate = page.Active;
        }

        foreach (var fd in dto.Floating)
        {
            var td = fd.Chart;
            var tab = await w.NewTabAsync(td.FeedId, td.Symbol, PeriodOf(td), SettingsOf(td), activate: false, accountId: td.AccountId, historyHours: td.HistoryHours);
            Finish(w, tab, td);
            w.Detach(tab, new Avalonia.PixelPoint(fd.X, fd.Y), fd.Width > 200 && fd.Height > 150 ? new Avalonia.Size(fd.Width, fd.Height) : null);
        }

        if (activate != null) w.Activate(activate);
        w.RefreshLinkButtons();
        return true;
    }

    // ===================================================================== dibujos (por instrumento)

    private static string DrawingsFile(ChartTab t) =>
        Path.Combine(AppPaths.Drawings, string.Concat($"{t.FeedId}_{t.Symbol}".Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".json");

    /// <summary>
    /// Una sola lista de dibujos por instrumento: todos los gráficos de ese instrumento (cualquier período,
    /// pestaña o ventana) la comparten, así se sincronizan y nunca se pisan al guardar.
    /// </summary>
    private static readonly Dictionary<string, List<ChartDrawing>> Shared = new();

    public static void SaveDrawings(ChartTab t)
    {
        if (t.Model == null) return;
        try
        {
            Directory.CreateDirectory(AppPaths.Drawings);
            File.WriteAllText(DrawingsFile(t), System.Text.Json.JsonSerializer.Serialize(t.Model.Drawings));
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    public static void LoadDrawings(ChartTab t)
    {
        if (t.Model == null) return;
        var f = DrawingsFile(t);
        if (!Shared.TryGetValue(f, out var list))
        {
            list = new List<ChartDrawing>();
            try
            {
                if (File.Exists(f)) list = System.Text.Json.JsonSerializer.Deserialize<List<ChartDrawing>>(File.ReadAllText(f)) ?? list;
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
            }

            Shared[f] = list;
        }

        t.Model.Drawings = list;
    }
}
