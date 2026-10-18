using TradingHub.Scripting;
using TradingHub.Strategies;

namespace TradingHub.App.Services;

/// <summary>Una estrategia en disco (un archivo .cs) y su última compilación.</summary>
public sealed class StrategyFile
{
    public required string Path { get; init; }
    public string FileName => System.IO.Path.GetFileNameWithoutExtension(Path);
    public Type? Type { get; set; }
    public ScriptCompileResult? LastCompile { get; set; }
    public DateTime CompiledFileTime { get; set; }
    public string DisplayName => Type != null ? Strategy.DisplayName(Type) : FileName;

    /// <summary>Id estable para el historial (el nombre de la clase).</summary>
    public string Id => Type?.FullName ?? FileName;

    public Strategy Create() => (Strategy)Activator.CreateInstance(Type ?? throw new InvalidOperationException("Strategy is not compiled."))!;
}

/// <summary>Estrategias del usuario: archivos en ~/Documents/Trading Hub/Estrategias, compilados con Roslyn.</summary>
public sealed class StrategyCatalog
{
    private readonly ScriptCompiler _compiler = new();

    public StrategyCatalog()
    {
        _ = typeof(Strategy); // asegura que TradingHub.Strategies esté cargado para referenciarlo al compilar
        // TRADINGHUB_BACKTESTS_DIR: historial alternativo (pruebas)
        StrategyStore.Root = Environment.GetEnvironmentVariable("TRADINGHUB_BACKTESTS_DIR") is { Length: > 0 } dir ? dir : AppPaths.Backtests;
        Directory.CreateDirectory(AppPaths.Strategies);
        if (!Directory.EnumerateFiles(AppPaths.Strategies, "*.cs").Any())
        {
            foreach (var t in StrategyTemplates.All.Where(t => t.Example))
                File.WriteAllText(System.IO.Path.Combine(AppPaths.Strategies, t.FileName + ".cs"), t.Code(t.FileName).Replace("__NOMBRE__", t.Name));
        }

        Refresh();
    }

    public List<StrategyFile> Files { get; } = new();

    public event Action? Changed;

    public void Refresh()
    {
        var existing = Files.ToDictionary(f => f.Path);
        Files.Clear();
        var extra = Enumerable.Empty<string>();
        foreach (var path in Directory.EnumerateFiles(AppPaths.Strategies, "*.cs").Concat(extra).OrderBy(p => p))
            Files.Add(existing.TryGetValue(path, out var f) ? f : new StrategyFile { Path = path });
        Changed?.Invoke();
    }

    /// <summary>Compila un archivo (si cambió desde la última vez o si se fuerza).</summary>
    public ScriptCompileResult Compile(StrategyFile file, bool force = false)
    {
        var stamp = File.GetLastWriteTimeUtc(file.Path);
        if (!force && file.LastCompile != null && stamp == file.CompiledFileTime) return file.LastCompile;
        var res = _compiler.Compile(new[] { file.Path }, "Estrategia_" + file.FileName);
        file.LastCompile = res;
        file.CompiledFileTime = stamp;
        if (res.Success && res.Assembly != null)
        {
            var types = res.Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(Strategy).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null).ToList();
            file.Type = types.FirstOrDefault();
            if (file.Type == null)
            {
                var diag = new ScriptDiagnostic(file.Path, 1, 1, "error", "TH01", "The file contains no public Strategy subclass with a parameterless constructor.");
                file.LastCompile = new ScriptCompileResult { Success = false, Diagnostics = res.Diagnostics.Append(diag).ToList(), Elapsed = res.Elapsed };
            }
        }
        else
        {
            file.Type = null;
        }

        Changed?.Invoke();
        return file.LastCompile;
    }

    /// <summary>Crea un archivo nuevo desde una plantilla y devuelve su ruta.</summary>
    public StrategyFile Create(string name, StrategyTemplate template)
    {
        string className = new string(name.Where(char.IsLetterOrDigit).ToArray());
        if (className.Length == 0 || char.IsDigit(className[0])) className = "Strategy" + className;
        var path = System.IO.Path.Combine(AppPaths.Strategies, className + ".cs");
        for (int i = 2; File.Exists(path); i++) path = System.IO.Path.Combine(AppPaths.Strategies, $"{className}{i}.cs");
        className = System.IO.Path.GetFileNameWithoutExtension(path);
        File.WriteAllText(path, template.Code(className).Replace("__NOMBRE__", name.Replace("\"", "'")));
        Refresh();
        return Files.First(f => f.Path == path);
    }

    public void Delete(StrategyFile file)
    {
        if (File.Exists(file.Path)) File.Delete(file.Path);
        Refresh();
    }
}

/// <summary>Plantilla de estrategia.</summary>
public sealed record StrategyTemplate(string Name, string Description, string FileName, bool Example, Func<string, string> Code);

/// <summary>Plantillas incluidas (también se usan como ejemplos la primera vez).</summary>
public static class StrategyTemplates
{
    public static readonly StrategyTemplate[] All =
    {
        new("Moving-average crossover", "Long when the fast average crosses above the slow average; short on the reverse. A starting example.", "MovingAverageCrossover", true, c => $$"""
            using TradingHub.Strategies;

            [Strategy("__NOMBRE__", Description = "Two exponential moving averages with stop and target distances in ticks.")]
            public class {{c}} : Strategy
            {
                [Parameter("Fast average"), Optimize(5, 30, 5)]
                public int FastPeriod { get; set; } = 10;

                [Parameter("Slow average"), Optimize(20, 100, 10)]
                public int SlowPeriod { get; set; } = 40;

                private Ema _fast = null!, _slow = null!;

                protected override void OnInit()
                {
                    _fast = Ema(FastPeriod);
                    _slow = Ema(SlowPeriod);
                }

                protected override void OnBar(Bar bar)
                {
                    if (!_slow.IsReady) return;

                    if (CrossAbove(_fast, _slow)) EnterLong();
                    else if (CrossBelow(_fast, _slow)) EnterShort();
                }
            }
            """),

        new("Range breakout", "Buys a breakout of the N-bar high and sells a breakout of the low, with an ATR stop.", "RangeBreakout", true, c => $$"""
            using TradingHub.Strategies;

            [Strategy("__NOMBRE__", Description = "Donchian channel breakout with an ATR exit.")]
            public class {{c}} : Strategy
            {
                [Parameter("Channel bars"), Optimize(10, 60, 10)]
                public int ChannelPeriod { get; set; } = 20;

                [Parameter("Stop distance (ATR multiple)"), Optimize(1, 4, 0.5)]
                public double StopAtrMultiple { get; set; } = 2;

                private Highest _highest = null!;
                private Lowest _lowest = null!;
                private Atr _atr = null!;
                private decimal _stop;

                protected override void OnInit()
                {
                    _highest = Highest(ChannelPeriod);
                    _lowest = Lowest(ChannelPeriod);
                    _atr = Atr(14);
                }

                protected override void OnBar(Bar bar)
                {
                    if (Bars.Count <= ChannelPeriod || !_atr.IsReady) return;
                    decimal upper = _highest[1], lower = _lowest[1];
                    decimal stopDistance = _atr.Value * (decimal)StopAtrMultiple;

                    if (IsFlat)
                    {
                        if (bar.Close > upper) { EnterLong(); _stop = bar.Close - stopDistance; }
                        else if (bar.Close < lower) { EnterShort(); _stop = bar.Close + stopDistance; }
                    }
                    else if (IsLong)
                    {
                        _stop = Math.Max(_stop, bar.Close - stopDistance);
                        if (bar.Close < _stop) ClosePosition("ATR stop");
                    }
                    else
                    {
                        _stop = Math.Min(_stop, bar.Close + stopDistance);
                        if (bar.Close > _stop) ClosePosition("ATR stop");
                    }
                }
            }
            """),

        new("Delta absorption", "Order flow: a strongly negative-delta bar closes higher (sell absorption) → buy, and vice versa.", "DeltaAbsorption", true, c => $$"""
            using TradingHub.Strategies;

            [Strategy("__NOMBRE__", Description = "Detects absorption: strong opposing aggressive volume fails to move the price.")]
            public class {{c}} : Strategy
            {
                [Parameter("Volume average period")]
                public int VolumeLookback { get; set; } = 20;

                [Parameter("Minimum volume (× average)"), Optimize(1.0, 3.0, 0.5)]
                public double MinimumVolumeMultiple { get; set; } = 1.5;

                [Parameter("Minimum delta (% of volume)"), Optimize(10, 40, 10)]
                public int MinimumDeltaPercent { get; set; } = 20;

                [Parameter("Maximum bars per trade")]
                public int MaxBarsInTrade { get; set; } = 6;

                private Sma _volume = null!;
                private int _entryBar;

                public {{c}}()
                {
                    StopLossTicks = 40;
                    TakeProfitTicks = 80;
                }

                protected override void OnInit()
                {
                    UseFootprint = true; // Per-price volume in each bar (bar.Levels, bar.PocPrice).
                    _volume = Sma(VolumeLookback, PriceSource.Volume);
                }

                protected override void OnBar(Bar bar)
                {
                    if (!_volume.IsReady) return;

                    if (!IsFlat && CurrentBar - _entryBar >= MaxBarsInTrade)
                    {
                        ClosePosition("Time");
                        return;
                    }

                    bool highVolume = bar.Volume >= _volume[1] * (decimal)MinimumVolumeMultiple;
                    decimal deltaPct = bar.Volume > 0 ? 100 * bar.Delta / bar.Volume : 0;

                    // Strong aggressive selling, but a close above POC indicates absorption.
                    if (IsFlat && highVolume && deltaPct <= -MinimumDeltaPercent && bar.Close > bar.PocPrice && bar.IsBullish)
                    {
                        EnterLong();
                        _entryBar = CurrentBar;
                    }
                    else if (IsFlat && highVolume && deltaPct >= MinimumDeltaPercent && bar.Close < bar.PocPrice && bar.IsBearish)
                    {
                        EnterShort();
                        _entryBar = CurrentBar;
                    }
                }
            }
            """),

        new("VWAP mean reversion", "Trades a return to session VWAP after price moves too far away, within a defined schedule.", "VwapReversion", true, c => $$"""
            using TradingHub.Strategies;

            [Strategy("__NOMBRE__", Description = "Sells extensions above VWAP and buys below; exits at VWAP.")]
            public class {{c}} : Strategy
            {
                [Parameter("Distance (× ATR)"), Optimize(1.0, 3.0, 0.5)]
                public double DistanceAtrMultiple { get; set; } = 2;

                [Parameter("From (local hour)")]
                public int StartHour { get; set; } = 10;

                [Parameter("To (local hour)")]
                public int EndHour { get; set; } = 20;

                private Vwap _vwap = null!;
                private Atr _atr = null!;

                public {{c}}()
                {
                    StopLossTicks = 60;
                }

                protected override void OnInit()
                {
                    _vwap = Vwap();
                    _atr = Atr(20);
                }

                protected override void OnBar(Bar bar)
                {
                    if (!_atr.IsReady) return;
                    bool withinTradingHours = TimeBetween(TimeSpan.FromHours(StartHour), TimeSpan.FromHours(EndHour));
                    decimal band = _atr.Value * (decimal)DistanceAtrMultiple;

                    if (IsLong && bar.Close >= _vwap.Value) ClosePosition("VWAP");
                    else if (IsShort && bar.Close <= _vwap.Value) ClosePosition("VWAP");
                    else if (IsFlat && withinTradingHours)
                    {
                        if (bar.Close < _vwap.Value - band) EnterLong();
                        else if (bar.Close > _vwap.Value + band) EnterShort();
                    }

                    // Do not hold a position outside trading hours.
                    if (!withinTradingHours && !IsFlat) ClosePosition("End of trading hours");
                }
            }
            """),

        new("Empty", "Minimal skeleton with available lifecycle methods.", "MyStrategy", false, c => $$"""
            using TradingHub.Strategies;

            [Strategy("__NOMBRE__", Description = "")]
            public class {{c}} : Strategy
            {
                // [Parameter("Name"), Optimize(min, max, step)] exposes a parameter to optimization.
                [Parameter("Period"), Optimize(10, 50, 10)]
                public int Period { get; set; } = 20;

                private Sma _average = null!;

                protected override void OnInit()
                {
                    // Indicators: Sma, Ema, Rsi, Atr, Vwap, Bollinger, Macd, Highest, Lowest, CumulativeDelta…
                    _average = Sma(Period);
                }

                protected override void OnBar(Bar bar)
                {
                    // bar = latest closed bar (Open, High, Low, Close, Volume, BuyVolume, SellVolume, Delta…)
                    // Orders: EnterLong(), EnterShort(), Buy(), Sell(), BuyLimit(price), SellStop(price), ClosePosition()
                    // Automatic stops: StopLossTicks, TakeProfitTicks, TrailingStopTicks
                }
            }
            """),
    };
}
