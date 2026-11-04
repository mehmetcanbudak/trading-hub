// Adapted from PANASV/trading-hub cdb84ea16eee7ac8d6c1b74136d2eeacedadf834 (MIT with Commons Clause).
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Data;

namespace TradingHub.Indicators.Native;

public enum CamarillaSessionMode
{
    [Display(Name = "Chart session")] Chart,
    [Display(Name = "CME (18:00 New York)")] Cme,
    [Display(Name = "Custom session")] Custom,
}

[DisplayName("QuanTAlib Camarilla")]
[Category("QuanTAlib")]
[Description("PP and H1–H6/L1–L6 from the previous observed session. Initial history may contain a partial session; no levels are invented before the first rollover.")]
public sealed class QtCamarilla : QtIndicator
{
    private readonly OptionalValueDataSeries[] _lines;
    private CamarillaSessionMode _mode;
    private string _timeZoneId = "America/New_York";
    private double _startHour = 18;
    private SessionSettings _session = CamarillaSessions.Create("America/New_York", 18);
    private CamarillaSessionState _state, _beforeBar;
    public QtCamarilla() : base(true)
    {
        DenyToChangePanel = true;
        string[] names = ["H3", "PP", "H1", "H2", "H4", "H5", "H6", "L1", "L2", "L3", "L4", "L5", "L6"];
        _lines = new OptionalValueDataSeries[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            var line = new OptionalValueDataSeries(name, name)
            {
                Color = name.StartsWith('H') ? Color.IndianRed : name.StartsWith('L') ? Color.MediumSeaGreen : Color.Silver,
                Width = name is "H3" or "L3" ? 2 : 1,
                Visible = name is not ("PP" or "H2" or "L2"),
            };
            _lines[i] = line;
            if (i == 0) DataSeries[0] = line; else DataSeries.Add(line);
        }
    }
    [Display(Name = "Session", GroupName = "Session", Order = 10)]
    public CamarillaSessionMode SessionMode
    {
        get => _mode;
        set { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); _mode = value; RecalculateValues(); }
    }
    [Display(Name = "Custom time zone", GroupName = "Session", Order = 20)]
    public string TimeZoneId
    {
        get => _timeZoneId;
        set { var session = CamarillaSessions.Create(value, _startHour); _timeZoneId = value; _session = session; RecalculateValues(); }
    }
    [Display(Name = "Custom start hour", GroupName = "Session", Order = 30), Range(0, 23.999999)]
    public double SessionStartHour
    {
        get => _startHour;
        set { var session = CamarillaSessions.Create(_timeZoneId, value); _startHour = value; _session = session; RecalculateValues(); }
    }
    [Browsable(false)] public bool IsReady => _state.IsReady;
    [Browsable(false)] public CamarillaLevels? Levels => _state.IsReady ? _state.Levels : null;
    protected override void ResetState() { _state = default; _beforeBar = default; }
    protected override void Step(int bar, decimal value, bool isNew)
    {
        if (isNew) _beforeBar = _state; else _state = _beforeBar;
        bool rollover = bar > 0 && (_mode == CamarillaSessionMode.Chart ? IsNewSession(bar) :
            (_mode == CamarillaSessionMode.Cme ? CmeSession : _session).IsNewSession(GetCandle(bar - 1).Time, GetCandle(bar).Time));
        var candle = GetCandle(bar);
        _state.Update(candle.High, candle.Low, candle.Close, rollover);
        foreach (var line in _lines)
        {
            if (bar > 0)
            {
                if (rollover) line.SetPointOfEndLine(bar - 1); else line.RemovePointOfEndLine(bar - 1);
            }
        }
        if (!_state.IsReady) { foreach (var line in _lines) line.SetUnavailable(bar); return; }
        var l = _state.Levels;
        _lines[0].Set(bar, l.H3); _lines[1].Set(bar, l.PP); _lines[2].Set(bar, l.H1); _lines[3].Set(bar, l.H2);
        _lines[4].Set(bar, l.H4); _lines[5].Set(bar, l.H5); _lines[6].Set(bar, l.H6);
        _lines[7].Set(bar, l.L1); _lines[8].Set(bar, l.L2); _lines[9].Set(bar, l.L3);
        _lines[10].Set(bar, l.L4); _lines[11].Set(bar, l.L5); _lines[12].Set(bar, l.L6);
    }
    private static readonly SessionSettings CmeSession = CamarillaSessions.Create("America/New_York", 18);
}
