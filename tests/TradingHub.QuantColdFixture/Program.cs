using System.Runtime.CompilerServices;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Scripting;
using TradingHub.Strategies;

// A dedicated process: no test runner or previously executed study can warm the dependency loader.
if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "QuanTAlib"))
    throw new InvalidOperationException("Cold-process prerequisite failed: QuanTAlib is already loaded.");
string directory = Path.Combine(Path.GetTempPath(), "TradingHub-quant-cold-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    string file = Path.Combine(directory, "ColdQuantStrategy.cs");
    File.WriteAllText(file, """
        using TradingHub.Strategies;
        public sealed class ColdQuantStrategy : Strategy
        {
            private QtSeries _jma = null!;
            private CamarillaSeries _camarilla = null!;
            public int ClosedBars { get; private set; }
            public int ReadyBars { get; private set; }
            protected override void OnInit()
            {
                _jma = Qt(new Q.Jma(4));
                _camarilla = Camarilla();
            }
            protected override void OnBar(Bar bar)
            {
                ClosedBars++;
                if (_jma.IsReady) ReadyBars++;
                if (_jma.IsReady && _jma.AvailableValue == null) throw new Exception("Ready numeric gap");
            }
        }
        """);
    var compiled = new ScriptCompiler().Compile([file], "ColdQuant");
    if (!compiled.Success) throw new InvalidOperationException(string.Join(Environment.NewLine, compiled.Diagnostics));
    Run(compiled.Assembly!.GetType("ColdQuantStrategy")!);
    Console.WriteLine("Cold ScriptCompiler Q alias + JMA + Camarilla + closed-bar BacktestEngine passed.");
    return 0;
}
finally { Directory.Delete(directory, recursive: true); }

[MethodImpl(MethodImplOptions.NoInlining)]
static void Run(Type strategyType)
{
    var strategy = (Strategy)Activator.CreateInstance(strategyType)!;
    var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    var instrument = new Instrument { Symbol = "QUANT-FAKE", FeedId = "test", TickSize = 1, VolumeStep = 1, PointValue = 1 };
    var trades = Enumerable.Range(0, 600).Select(i => new Trade(i + 1, start.AddMinutes(i), 100 + i % 17, 1, TradeSide.Buy)).ToList();
    var result = new BacktestEngine(strategy, TickData.FromTrades(instrument, trades), new BacktestSettings
    {
        FeedId = "test", Symbol = instrument.Symbol, Period = "M1", FromUtc = start,
        ToUtc = start.AddDays(1), InitialCapital = 1000, CommissionPercent = 0, SlippageTicks = 0,
    }).Run();
    if (result.Error != null) throw new InvalidOperationException(result.Error);
    if ((int)strategyType.GetProperty("ClosedBars")!.GetValue(strategy)! != 600 ||
        (int)strategyType.GetProperty("ReadyBars")!.GetValue(strategy)! <= 0)
        throw new InvalidOperationException("Cold strategy did not consume all closed bars and become library-ready.");
    if (result.Trades.Count != 0) throw new InvalidOperationException("A data-only fixture submitted orders.");
}
