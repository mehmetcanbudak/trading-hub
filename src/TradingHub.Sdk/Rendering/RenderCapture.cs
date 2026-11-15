using System.Drawing;

namespace TradingHub.Rendering.Context
{
    /// <summary>
    /// Receives what an indicator draws through <see cref="RenderContext"/> (pixel coordinates). Implementations copy what
    /// they keep: the arrays passed in belong to the indicator.
    /// </summary>
    internal interface IRenderCapture
    {
        void Line(float x1, float y1, float x2, float y2, Color color);
        void Lines(Point[] points, Color color);
        void Lines(PointF[] points, Color color);
        void Text(string text, RectangleF box);
    }
}
