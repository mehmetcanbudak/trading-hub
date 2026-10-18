// Escena "panel": el panel derecho de Trading con Chart Trader, DOM y cinta a la vez (cada uno con su alto y su
// desplazamiento), los botones del DOM, las tres secciones en ventanas aparte y el gráfico pegado al panel.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using TradingHub.App.Controls;
using TradingHub.App.Windows;

internal static class PanelScene
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
        var tv = window.Trading;
        foreach (var key in new[] { "ticket", "dom", "tape" }) tv.SetPanel(key, true);
        pump(2);
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "panel_tres.png"));

        Control? Section(string title) => tv.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text == title.ToUpperInvariant() && t.IsEffectivelyVisible)
            ?.GetVisualAncestors().OfType<DockPanel>().FirstOrDefault(d => d.Parent is Border or Grid);
        var ticket = Section("Chart Trader");
        var dom = tv.GetVisualDescendants().OfType<DomView>().FirstOrDefault();
        var tape = tv.GetVisualDescendants().OfType<TapeView>().FirstOrDefault();
        Console.WriteLine($"Alturas: ticket {ticket?.Bounds.Height:0} · DOM {dom?.Bounds.Height:0} · cinta {tape?.Bounds.Height:0}");
        Check(ticket is { Bounds.Height: > 80 } && dom is { Bounds.Height: > 60, IsEffectivelyVisible: true } && tape is { Bounds.Height: > 60, IsEffectivelyVisible: true },
            "con los tres activados se ven los tres (ninguno tapa a otro)");

        // el Chart Trader se desliza
        var sv = ticket?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        Check(sv != null && sv.Extent.Height > sv.Viewport.Height + 20, $"el Chart Trader tiene más contenido que alto (contenido {sv?.Extent.Height:0}, a la vista {sv?.Viewport.Height:0})");
        if (sv != null)
        {
            sv.Offset = new Vector(0, 10_000);
            pump(0.5);
            Check(sv.Offset.Y > 0 && sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 1, $"el Chart Trader se desliza hasta abajo ({sv.Offset.Y:0} px de {sv.Extent.Height - sv.Viewport.Height:0})");
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "panel_ticket_abajo.png"));
            sv.Offset = new Vector(0, 0);
        }

        // la cinta se desliza con la rueda y vuelve a lo último con un clic arriba
        if (tape?.Tape is { } model)
        {
            pump(1);
            int count = model.Data.Trades.Count;
            var center = tape.TranslatePoint(new Point(tape.Bounds.Width / 2, tape.Bounds.Height / 2), window) ?? default;
            window.MouseWheel(center, new Vector(0, -3));
            pump(0.5);
            Check(model.Anchor != null && model.Anchor < count - 1, $"la cinta se desliza hacia operaciones más viejas ({count} operaciones, arriba la {model.Anchor})");
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "panel_cinta_desplazada.png"));
            var top = tape.TranslatePoint(new Point(tape.Bounds.Width / 2, 8), window) ?? default;
            window.MouseDown(top, MouseButton.Left);
            window.MouseUp(top, MouseButton.Left);
            pump(0.3);
            Check(model.Anchor == null, "un clic en el encabezado vuelve a lo último");
        }
        else Check(false, "la cinta tiene datos");

        // botones del DOM
        var domButtons = tv.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text is "B. Bid" or "S. Ask" or "Close")).ToList();
        Check(domButtons.Count >= 3, $"el DOM tiene botones de ejecución ({domButtons.Count})");

        // el gráfico pegado al panel: sin hueco entre el borde del gráfico y el del panel
        var frame = tv.ActiveTab?.Frame;
        var side = dom?.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.BorderThickness.Left == 1 && b.Child is Grid);
        if (frame != null && side != null)
        {
            double frameRight = (frame.TranslatePoint(new Point(frame.Bounds.Width, 0), window) ?? default).X;
            double sideLeft = (side.TranslatePoint(new Point(0, 0), window) ?? default).X;
            Check(Math.Abs(sideLeft - frameRight) < 1.5, $"el gráfico queda pegado al panel (gráfico termina en {frameRight:0}, panel empieza en {sideLeft:0})");
        }
        else Check(false, "encuentra el gráfico y el panel");

        // las tres secciones en ventanas aparte
        foreach (var key in new[] { "ticket", "tape", "dom" })
        {
            tv.DetachSection(key);
            pump(1);
            var w = tv.PanelWindow(key);
            Check(w is { IsVisible: true }, $"{key} en una ventana aparte ({w?.Title})");
            w?.CaptureRenderedFrame()?.Save(Path.Combine(outDir, $"panel_ventana_{key}.png"));
        }

        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "panel_todo_afuera.png"));
        foreach (var key in new[] { "ticket", "tape", "dom" })
        {
            tv.PanelWindow(key)?.Close();
            pump(0.5);
        }

        pump(1);
        dom = tv.GetVisualDescendants().OfType<DomView>().FirstOrDefault();
        tape = tv.GetVisualDescendants().OfType<TapeView>().FirstOrDefault();
        Check(tv.PanelWindow("ticket") == null && dom is { IsEffectivelyVisible: true } && tape is { IsEffectivelyVisible: true }, "al cerrar las ventanas vuelven al panel");
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "panel_de_vuelta.png"));

        // la barra del gráfico: tipo de gráfico en un selector, períodos favoritos en fila y el resto en su selector
        var toolbar = tv.Toolbar;
        var settings = TradingHub.App.App.Services.Settings;
        settings.FavoritePeriods = new() { "M1", "M5", "M15", "H1", "H4", "Daily" };
        toolbar.Sync();
        pump(0.5);
        var barTexts = toolbar.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Check(barTexts.Count(t => t is "Candles" or "Bars" or "Line" or "Heikin Ashi" or "Footprint") == 1 && !barTexts.Contains("30m") && barTexts.Contains("15m"),
            $"la barra muestra solo los períodos favoritos y el tipo de gráfico va en un selector ({string.Join(" | ", barTexts)})");
        var menuBtn = (Button)typeof(TradingHub.App.Views.ChartToolbar).GetField("_periodMenu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(toolbar)!;
        var fly = (Flyout)typeof(TradingHub.App.Views.ChartToolbar).GetField("_periodFlyout", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(toolbar)!;
        fly.ShowAt(menuBtn);
        pump(1);
        var menu = fly.Content as Control;
        TopLevel.GetTopLevel(menu)?.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "barra_periodos.png"));
        // clic derecho sobre 30m lo pone en la barra
        var pick30 = menu?.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == "30m");
        var popupTop = TopLevel.GetTopLevel(menu);
        if (pick30 != null && popupTop != null)
        {
            var at = pick30.TranslatePoint(new Point(pick30.Bounds.Width / 2, pick30.Bounds.Height / 2), popupTop) ?? default;
            popupTop.MouseDown(at, MouseButton.Right);
            popupTop.MouseUp(at, MouseButton.Right);
        }
        pump(0.5);
        fly.Hide();
        pump(0.3);
        barTexts = toolbar.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Check(settings.FavoritePeriods.Contains("M30") && barTexts.Contains("30m"), "clic derecho agrega el período a la barra");
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "barra_grafico.png"));
        settings.FavoritePeriods.Remove("M30");
        toolbar.Sync();

        // el cartel de confirmación (Cuenta → Reiniciar la cuenta simulada): con aire, Cancelar y la acción
        window.Navigate("accounts");
        pump(1);
        var restart = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == "Reset simulated account");
        if (restart?.Flyout is Flyout confirm)
        {
            confirm.ShowAt(restart);
            pump(0.8);
            var content = confirm.Content as Control;
            var labels = content?.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).ToList() ?? new();
            Check(labels.Contains("Cancel") && labels.Contains("Reset") && content is StackPanel { Margin.Left: >= 12 }, $"el cartel tiene margen, «Cancelar» y la acción ({string.Join(", ", labels)})");
            TopLevel.GetTopLevel(content)?.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "cartel_confirmar.png"));
            confirm.Hide();
        }
        else Check(false, "encuentra el botón de reiniciar la cuenta simulada");

        Console.WriteLine(Failures.Count == 0 ? "PANEL: todo OK" : $"PANEL: {Failures.Count} falla(s)");
        return Failures.Count;
    }
}
