using Avalonia.Controls;

namespace TradingHub.App.Controls;

/// <summary>Modelos de Claude y niveles de esfuerzo para los agentes, y el combo para elegirlos.</summary>
internal static class AgentChoices
{
    internal static readonly (string Id, string Label)[] Models =
    {
        ("claude-opus-5-5", "Opus 5.5"), ("claude-sonnet-5-5", "Sonnet 5.5"), ("claude-haiku-5-5", "Haiku 5.5"), ("claude-fable-5-1", "Fable 5.1"),
    };

    internal static readonly (string Id, string Label)[] Efforts =
    {
        ("low", "Low"), ("medium", "Medium"), ("high", "High"), ("xhigh", "Extra"), ("max", "Maximum"),
    };

    internal static ComboBox Combo((string Id, string Label)[] options, string current, Action<string> changed, double width = 150)
    {
        var box = new ComboBox { Width = width };
        foreach (var (id, label) in options) box.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        if (options.All(o => o.Id != current) && current.Length > 0) box.Items.Add(new ComboBoxItem { Content = current, Tag = current });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == current) ?? box.Items[0];
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Tag: string v }) changed(v);
        };
        return box;
    }
}
