using TradingHub.Rendering.Context;

namespace TradingHub.Indicators
{
    /// <summary>
    /// One action an indicator adds to the chart's right-click menu. <see cref="Text"/> is English source text; the host
    /// translates it like any other menu header. The host saves the workspace after <see cref="Run"/>.
    /// </summary>
    public sealed record IndicatorMenuItem(string Text, Action Run, bool Checked = false);

    public abstract partial class Indicator
    {
        /// <summary>
        /// Indicator-specific actions for a right-click at chart pixel (<paramref name="x"/>, <paramref name="y"/>),
        /// for example merging volume profiles. Empty by default.
        /// </summary>
        public virtual IReadOnlyList<IndicatorMenuItem> GetChartMenuItems(int x, int y) => Array.Empty<IndicatorMenuItem>();

        /// <summary>Draws a child registered with <see cref="Add"/> from the parent's <c>OnRender</c>.</summary>
        protected void RenderChild(Indicator child, RenderContext context, DrawingLayouts layout)
        {
            if (child != null && child._parent == this) child.OnRender(context, layout);
        }
    }
}
