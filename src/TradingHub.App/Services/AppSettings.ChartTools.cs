namespace TradingHub.App.Services;

// Chart toolbar and drawing bar preferences shared by every chart.
public sealed partial class AppSettings
{
    /// <summary>Favorite indicators (full type names): the toolbar star adds them with one click.</summary>
    public List<string> FavoriteIndicators { get; set; } = new();

    /// <summary>Drawing magnet from the drawing bar (all charts).</summary>
    public TradingHub.Charting.MagnetMode DrawingMagnet { get; set; }

    /// <summary>Keep drawing: the tool stays active after each drawing.</summary>
    public bool DrawingSticky { get; set; }

    /// <summary>Gesture drawing: hold still on the chart, then trace a shape. Off by default so a press-and-hold pan keeps panning.</summary>
    public bool InkGestures { get; set; }

    /// <summary>Show the selected-drawing bar (color, width…) right after finishing a drawing.</summary>
    public bool DrawingBarAfterDrawing { get; set; } = true;

    /// <summary>Favorite drawing tools (enum names), grouped together on the drawing bar.</summary>
    public List<string> FavoriteDrawingTools { get; set; } = new();
}
