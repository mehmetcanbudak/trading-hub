// Escena "esquinas": cómo recorta Avalonia el contenido en bordes redondeados (para arreglar las puntas).
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;
using TradingHub.App.Windows;

internal static class CornersScene
{
    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        TradingHub.App.Services.AppTheme.Apply(TradingHub.App.Services.AppTheme.Light);
        var num = new NumericUpDown { Value = 20, Width = 220, Margin = new Thickness(20), Increment = 1 };
        var combo = new ComboBox { Width = 220, Margin = new Thickness(20), Items = { "Uno", "Dos" }, SelectedIndex = 0 };
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { num, combo } };
        var w = new Window { Width = 600, Height = 120, Content = row, Background = Brushes.White };
        w.Show();
        pump(1);
        foreach (var v in num.GetVisualDescendants())
        {
            string extra = v switch
            {
                Border b => $" CornerRadius={b.CornerRadius} BorderThickness={b.BorderThickness} Bg={b.Background} Clip={b.ClipToBounds}",
                TemplatedControl t => $" CornerRadius={t.CornerRadius} Bg={t.Background} BorderThickness={t.BorderThickness}",
                ContentPresenter p => $" CornerRadius={p.CornerRadius} Bg={p.Background}",
                _ => "",
            };
            Console.WriteLine($"{new string(' ', 2 * Depth(v, num))}{v.GetType().Name} #{(v as Control)?.Name}{extra} {v.Bounds}");
        }

        using (var capturedFrame = w.CaptureRenderedFrame()) capturedFrame?.Save(Path.Combine(outDir, "esquinas.png"));
        w.Close();
        return 0;
    }

    private static int Depth(Visual v, Visual root)
    {
        int d = 0;
        for (var p = v.GetVisualParent(); p != null && p != root; p = p.GetVisualParent()) d++;
        return d;
    }
}
