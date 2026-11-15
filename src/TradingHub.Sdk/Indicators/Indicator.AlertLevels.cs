namespace TradingHub.Indicators
{
    /// <summary>
    /// A horizontal level an indicator draws itself (a naked POC, a key level, a session high, a zone edge…) that the user
    /// may attach an alert to. <see cref="Id"/> must stay identical across recalculations of the same level (derive it from
    /// the level kind and its source bar time, never from a mutable price). <see cref="Name"/> is English source text the
    /// host translates. The level exists between chart bars <see cref="FromBar"/> and <see cref="ToBar"/> inclusive;
    /// <c>int.MaxValue</c> extends it to the right edge.
    /// </summary>
    public readonly record struct IndicatorLevel(string Id, string Name, decimal Price, int FromBar = 0, int ToBar = int.MaxValue);

    public abstract partial class Indicator
    {
        /// <summary>
        /// Alertable levels this indicator draws by hand (not its data series). By default, the levels of child
        /// indicators registered with <see cref="Add"/>, their Id prefixed with the child index ("0.", "1." …).
        /// Called from the UI thread after calculation; implementations must not mutate state.
        /// </summary>
        public virtual IEnumerable<IndicatorLevel> GetAlertLevels()
        {
            for (int i = 0; i < _children.Count; i++)
                foreach (var level in _children[i].GetAlertLevels())
                    yield return level with { Id = i + "." + level.Id };
        }
    }
}
