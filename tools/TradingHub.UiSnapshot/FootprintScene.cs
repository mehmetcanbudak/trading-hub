// Escena "footprint": escaleras de bid, ask, delta y volumen con colores de texto propios, y el editor de footprint.
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using TradingHub.App.Windows;
using TradingHub.Charting;

internal static class FootprintScene
{
    private static readonly List<string> Failures = new();

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "OK " : "FALLA")}  {what}");
        if (!ok) Failures.Add(what);
    }

    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        window.Navigate("trading");
        pump(1);
        var tv = window.Trading;
        var model = tv.ActiveTab?.Model;
        if (model == null)
        {
            Check(false, "hay un gráfico abierto");
            return 1;
        }

        const uint Yellow = 0xFFFFD54F, Cyan = 0xFF4DD0E1, Green = 0xFF00E676, Pink = 0xFFFF4081, Orange = 0xFFFF9800;
        model.Settings.Style = ChartStyle.Footprint;
        model.Settings.Layout = new FootprintLayout
        {
            Name = "Text colors",
            Columns =
            {
                new() { Kind = FootprintColumnKind.Candle },
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Bid, Align = ColumnTextAlign.Right, BidTextColor = Yellow },
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Ask, Align = ColumnTextAlign.Left, AskTextColor = Cyan },
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Delta, Align = ColumnTextAlign.Right, PositiveTextColor = Green, NegativeTextColor = Pink },
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Volume, Align = ColumnTextAlign.Right, TextColor = Orange },
            },
        };
        model.Viewport.BarWidth = 260;
        model.Viewport.ScrollBars = 0;
        model.Invalidate();
        pump(2);
        var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(outDir, "footprint_colores.png"));

        // los colores elegidos aparecen en el gráfico (se buscan píxeles de cada uno)
        if (frame != null)
        {
            var path = Path.Combine(outDir, "footprint_colores.png");
            using var bmp = SkiaSharp.SKBitmap.Decode(path);
            bool Has(uint argb)
            {
                var want = new SkiaSharp.SKColor(argb);
                for (int y = 0; y < bmp.Height; y += 1)
                for (int x = 0; x < bmp.Width; x += 1)
                {
                    var c = bmp.GetPixel(x, y);
                    if (Math.Abs(c.Red - want.Red) < 12 && Math.Abs(c.Green - want.Green) < 12 && Math.Abs(c.Blue - want.Blue) < 12) return true;
                }

                return false;
            }

            Check(Has(Yellow), "texto del bid en amarillo");
            Check(Has(Cyan), "texto del ask en celeste");
            Check(Has(Green) || Has(Pink), "texto del delta en verde / rosa");
            Check(Has(Orange), "texto del volumen en naranja");
        }

        // el editor: la escalera de delta muestra sus colores de texto (y no los que no le tocan)
        var editor = new FootprintEditorWindow(model, tv) { Width = 1000, Height = 900 };
        editor.Show(window);
        pump(1);
        var list = editor.GetVisualDescendants().OfType<ListBox>().First();
        list.SelectedIndex = 3;
        pump(1);
        var labels = editor.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Check(labels.Contains("Positive delta text") && labels.Contains("Negative delta text") && labels.Contains("Text color"), "la escalera de delta tiene color del texto, delta positivo y negativo");
        Check(!labels.Contains("Bid text") && !labels.Contains("Bar direction"), "no muestra opciones que no le tocan");
        editor.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "footprint_editor_delta.png"));
        list.SelectedIndex = 1;
        pump(1);
        labels = editor.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Check(labels.Contains("Bid text") && !labels.Contains("Ask text"), "la escalera de bid tiene su color de texto");
        editor.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "footprint_editor_bid.png"));
        editor.Close();

        // los colores se guardan en la plantilla (JSON) y vuelven
        var json = System.Text.Json.JsonSerializer.Serialize(model.Settings.Layout);
        var back = System.Text.Json.JsonSerializer.Deserialize<FootprintLayout>(json)!;
        Check(back.Columns[3].PositiveTextColor == Green && back.Columns[4].TextColor == Orange, "los colores de texto se guardan con la plantilla");

        // la biblioteca de indicadores: sin etiquetas de origen en los de la biblioteca
        var iw = new IndicatorsWindow(TradingHub.App.App.Services, model) { Width = 1100, Height = 760 };
        iw.Show(window);
        pump(1);
        var pills = iw.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text is "ATAS" or "extra" or "Trading Hub" && t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Check(!pills.Contains("ATAS"), $"la biblioteca de indicadores no muestra «ATAS» ({string.Join(", ", pills)})");
        iw.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "indicadores.png"));
        iw.Close();

        Console.WriteLine(Failures.Count == 0 ? "FOOTPRINT: todo OK" : $"FOOTPRINT: {Failures.Count} falla(s)");
        return Failures.Count;
    }
}
