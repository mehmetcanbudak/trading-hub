using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Localization;
using TradingHub.App.Services;

namespace TradingHub.App.Windows;

/// <summary>The grouped library: user-ordered groups, custom group names, item order, and editable copies of built-ins.</summary>
public sealed partial class IndicatorsWindow
{
    /// <summary>Catalog source of DLL indicators loaded from the Indicators folders (AppServices.ScanFolder).</summary>
    private const string ExtraSource = "Extra";

    private IndicatorInfo? _dragFrom;
    private Point _dragStart;
    private bool _dragging;
    private ListBoxItem? _dropOn;

    private IndicatorLibraryLayout Layout => _svc.Settings.IndicatorLibrary;

    private void ConfigureLibraryList()
    {
        _available.DoubleTapped += (_, _) =>
        {
            if (_available.SelectedItem is ListBoxItem { Tag: IndicatorInfo }) AddSelected();
        };
        _available.KeyDown += (_, e) =>
        {
            if (e.KeyModifiers != KeyModifiers.Alt || e.Key is not (Key.Up or Key.Down)) return;
            if (_available.SelectedItem is not ListBoxItem { Tag: IndicatorInfo info } || (_search.Text ?? "").Trim().Length > 0) return;
            ShiftItem(info, e.Key == Key.Up ? -1 : +1);
            e.Handled = true;
        };
    }

    /// <summary>Saves the arrangement additively in settings and redraws the list.</summary>
    private void LibraryChanged(IndicatorInfo? select = null)
    {
        _svc.Settings.Save();
        FillAvailable();
        if (select != null) _available.SelectedItem = _available.Items.OfType<ListBoxItem>().FirstOrDefault(x => Equals(x.Tag, select));
    }

    private static bool Matches(IndicatorInfo i, string q) =>
        q.Length == 0 || i.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || i.Category.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
        Lang.T(i.Name).Contains(q, StringComparison.CurrentCultureIgnoreCase) || Lang.T(i.Category).Contains(q, StringComparison.CurrentCultureIgnoreCase);

    private void FillAvailable()
    {
        var q = (_search.Text ?? "").Trim();
        _available.Items.Clear();
        int count = 0;
        foreach (var g in IndicatorLibrary.Build(_svc.Catalog.Items, Layout))
        {
            var items = g.Items.Where(i => Matches(i, q)).ToList();
            if (q.Length > 0 && items.Count == 0) continue; // while searching, only groups with matches
            count += items.Count;
            bool collapsed = g.Collapsed && q.Length == 0;
            _available.Items.Add(GroupHeader(g, collapsed, items.Count));
            if (collapsed) continue;
            foreach (var i in items) _available.Items.Add(LibraryItem(i, g, draggable: q.Length == 0));
        }

        _count.Text = $"{count}";
    }

    private ListBoxItem LibraryItem(IndicatorInfo i, IndicatorLibraryGroup group, bool draggable)
    {
        var row = new DockPanel();
        // no source badge for bundled indicators; custom, extra and updated-original copies are labelled
        var copy = _svc.SourceCopyFor(i.Type.FullName!);
        string? tag = copy?.State == SourceCopyState.OriginalUpdated ? "original updated"
            : i.Source == UserScripts.Source ? "custom"
            : i.Source == ExtraSource ? "extra" : null;
        if (tag != null)
        {
            var badge = new Border { Child = new TextBlock { Text = tag, FontSize = 10 }, Classes = { "pill" }, Padding = new Thickness(6, 1) };
            if (copy?.State == SourceCopyState.OriginalUpdated) badge.BorderBrush = Ui.Warn;
            DockPanel.SetDock(badge, Dock.Right);
            row.Children.Add(badge);
        }

        // favorites carry a star (the chart toolbar star adds them with one click)
        if (_svc.Settings.FavoriteIndicators.Contains(i.Type.FullName!))
        {
            var star = Ui.Icon(MaterialIconKind.Star, 13, Ui.Accent);
            star.Margin = new Thickness(0, 0, 6, 0);
            star.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(star, Dock.Left);
            row.Children.Add(star);
        }

        row.Children.Add(new TextBlock { Text = i.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var item = new ListBoxItem { Content = row, Tag = i, Padding = new Thickness(18, 4, 8, 4) };
        if (!string.IsNullOrWhiteSpace(i.Description)) ToolTip.SetTip(item, i.Description);
        item.ContextMenu = new ContextMenu { ItemsSource = ItemMenu(i, group, item) };
        if (draggable) Draggable(item, i);
        return item;
    }

    // ------------------------------------------------------------------ groups

    private ListBoxItem GroupHeader(IndicatorLibraryGroup g, bool collapsed, int count)
    {
        var chevron = Ui.Icon(collapsed ? MaterialIconKind.ChevronRight : MaterialIconKind.ChevronDown, 15, Ui.Text2);
        chevron.Margin = new Thickness(0, 0, 4, 0);
        var name = new TextBlock { Text = (g.IsUser ? g.Name : Lang.T(g.Name)).ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var n = new TextBlock { Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture), Classes = { "faint", "small" }, VerticalAlignment = VerticalAlignment.Center };
        var row = new DockPanel();
        DockPanel.SetDock(chevron, Dock.Left);
        DockPanel.SetDock(n, Dock.Right);
        row.Children.Add(chevron);
        row.Children.Add(n);
        row.Children.Add(name);
        var header = new ListBoxItem { Content = row, Tag = g, Cursor = new Cursor(StandardCursorType.Hand) };
        header.Classes.Add("header");
        ToolTip.SetTip(header, "Click to open or close the group. Right-click to rename, reorder or create groups. Drag indicators here to move them.");
        header.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || _dragging) return;
            IndicatorLibrary.SetCollapsed(Layout, g.Id, !collapsed);
            LibraryChanged();
            e.Handled = true;
        };
        header.ContextMenu = new ContextMenu { ItemsSource = GroupMenu(g) };
        return header;
    }

    private static MenuItem Item(string text, MaterialIconKind icon, Action run, bool enabled = true)
    {
        var item = new MenuItem { Header = text, Icon = Ui.Icon(icon, 16), IsEnabled = enabled };
        item.Click += (_, _) => run();
        return item;
    }

    private List<Control> GroupMenu(IndicatorLibraryGroup g)
    {
        var all = _svc.Catalog.Items;
        var items = new List<Control>
        {
            Item("Rename group…", MaterialIconKind.RenameOutline, async () =>
            {
                var name = await PromptDialog.ShowAsync(this, "Rename group", "Group name (empty restores the default name)", g.IsUser ? g.Name : Lang.T(g.Name), "Rename");
                if (name == null) return;
                IndicatorLibrary.RenameGroup(Layout, g.Id, name);
                LibraryChanged();
            }),
            Item("New group…", MaterialIconKind.FolderPlusOutline, async () =>
            {
                var name = await PromptDialog.ShowAsync(this, "New group", "Group name (placed below this group)", "", "Create");
                if (string.IsNullOrWhiteSpace(name)) return;
                IndicatorLibrary.CreateGroup(Layout, all, name, g.Id);
                LibraryChanged();
            }),
            new Separator(),
            Item("Move group up", MaterialIconKind.ArrowUp, () => { IndicatorLibrary.ShiftGroup(Layout, all, g.Id, -1); LibraryChanged(); }),
            Item("Move group down", MaterialIconKind.ArrowDown, () => { IndicatorLibrary.ShiftGroup(Layout, all, g.Id, +1); LibraryChanged(); }),
            Item("Sort by name (A → Z)", MaterialIconKind.SortAlphabeticalAscending, () => { IndicatorLibrary.SortGroup(Layout, all, g.Id); LibraryChanged(); }, g.Items.Count > 1),
        };
        if (g.IsUser)
        {
            items.Add(new Separator());
            items.Add(Item("Delete group (its indicators return to their default groups)", MaterialIconKind.FolderRemoveOutline, () =>
            {
                IndicatorLibrary.DeleteGroup(Layout, g.Id);
                LibraryChanged();
            }));
        }

        items.Add(new Separator());
        items.Add(Item("Restore the default library arrangement…", MaterialIconKind.Restore, async () =>
        {
            if (!await ConfirmDialog.ShowAsync(this, "Default library arrangement",
                    "Removes your groups, group names and ordering. Indicators on charts, saved charts and templates do not change.", "Restore defaults", danger: true)) return;
            IndicatorLibrary.Reset(Layout);
            LibraryChanged();
        }));
        return items;
    }

    // ------------------------------------------------------------------ items

    private List<Control> ItemMenu(IndicatorInfo i, IndicatorLibraryGroup current, ListBoxItem row)
    {
        var all = _svc.Catalog.Items;
        string type = i.Type.FullName!;
        var items = new List<Control>
        {
            Item("Add to chart", MaterialIconKind.Plus, () => { _available.SelectedItem = row; AddSelected(); }),
            Item(i.Source == UserScripts.Source ? "Edit source…" : "View source…", MaterialIconKind.CodeBraces, () => _ = SourceAsync(i)),
        };
        if (i.Source != UserScripts.Source)
            items.Add(Item("Edit a copy…", MaterialIconKind.PencilOutline, () => _ = EditCopyAsync(i)));
        bool favorite = _svc.Settings.FavoriteIndicators.Contains(type);
        items.Add(Item(favorite ? "Remove from favorites" : "Add to favorites (the chart toolbar star)",
            favorite ? MaterialIconKind.StarOffOutline : MaterialIconKind.StarOutline, () => ToggleFavorite(i)));
        if (_svc.SourceCopyFor(type) is { State: SourceCopyState.OriginalUpdated } copy)
        {
            items.Add(new Separator());
            items.Add(Item("The original was updated: restore it (your edits move to a backup)…", MaterialIconKind.Restore, () => _ = RestoreOriginalAsync(copy)));
            items.Add(Item("Keep my edits and stop reminding me", MaterialIconKind.ContentSaveCheckOutline, () => _ = KeepEditsAsync(copy)));
        }

        items.Add(new Separator());
        var move = new MenuItem { Header = "Move to group", Icon = Ui.Icon(MaterialIconKind.FolderMoveOutline, 16) };
        var targets = new List<Control>();
        foreach (var g in IndicatorLibrary.Build(all, Layout).Where(g => g.Id != current.Id))
        {
            string id = g.Id;
            var target = new MenuItem { Header = g.IsUser ? g.Name : Lang.T(g.Name) };
            target.Click += (_, _) => { IndicatorLibrary.Move(Layout, all, type, id, null); LibraryChanged(i); };
            targets.Add(target);
        }

        targets.Add(new Separator());
        targets.Add(Item("New group…", MaterialIconKind.FolderPlusOutline, async () =>
        {
            var name = await PromptDialog.ShowAsync(this, "New group", "Name of the new group", "", "Create and move");
            if (string.IsNullOrWhiteSpace(name)) return;
            var id = IndicatorLibrary.CreateGroup(Layout, all, name, current.Id);
            IndicatorLibrary.Move(Layout, all, type, id, null);
            LibraryChanged(i);
        }));
        move.ItemsSource = targets;
        items.Add(move);
        var up = Item("Move up", MaterialIconKind.ArrowUp, () => ShiftItem(i, -1));
        up.InputGesture = new KeyGesture(Key.Up, KeyModifiers.Alt);
        var down = Item("Move down", MaterialIconKind.ArrowDown, () => ShiftItem(i, +1));
        down.InputGesture = new KeyGesture(Key.Down, KeyModifiers.Alt);
        items.Add(up);
        items.Add(down);
        return items;
    }

    private void ShiftItem(IndicatorInfo i, int delta)
    {
        IndicatorLibrary.Shift(Layout, _svc.Catalog.Items, i.Type.FullName!, delta);
        LibraryChanged(i);
    }

    private void ShowLibraryError(string text)
    {
        _errorText.Text = text;
        _errorText.IsVisible = true;
    }

    /// <summary>
    /// Opens the existing tracked copy of a built-in, or makes one (a separate type; the original stays in the library and
    /// in saved charts). The copy follows future versions of the original while it is unedited.
    /// </summary>
    private async Task EditCopyAsync(IndicatorInfo original)
    {
        try
        {
            string originalType = original.Type.FullName!;
            var existing = _svc.SourceCopies.FirstOrDefault(s => s.OriginalType == originalType && Directory.Exists(s.Directory));
            string path;
            if (existing != null) path = Path.Combine(existing.Directory, existing.Manifest.EntryFile);
            else
            {
                _subtitle.Text = "Checking source and its complete dependency closure…";
                var copy = await Task.Run(() => IndicatorSources.ForAssembly(original.Type.Assembly).Fork(originalType, original.Type.Name + "Edited", AppPaths.UserIndicators));
                path = copy.EntryPath;
                await _svc.ReviewSourceCopiesAsync();
                _svc.Scripts?.CompileAll();
                _subtitle.Text = "Created a separate editable copy. The original stays available.";
            }

            new CodeEditorWindow(_svc, CodeKind.Indicator, open: path).Show(this);
        }
        catch (Exception ex)
        {
            ShowLibraryError("Could not make an editable copy: " + ex.Message);
        }
    }

    private async Task RestoreOriginalAsync(SourceCopyStatus copy)
    {
        if (!await ConfirmDialog.ShowAsync(this, "Restore the updated original",
                $"Replaces the edited copy {copy.Manifest.ClassName} with the current original under the same type, so charts using it keep working. Your edited files are moved to the «{IndicatorSourceCopies.BackupFolderName}» folder next to your indicator folder.",
                "Restore original", danger: true)) return;
        try
        {
            string backup = await Task.Run(() => IndicatorSourceCopies.RestoreOriginal(AppPaths.UserIndicators, copy.Directory));
            await _svc.ReviewSourceCopiesAsync();
            _svc.Scripts?.CompileAll();
            _subtitle.Text = $"Original restored. Your previous edits: {backup}";
        }
        catch (Exception ex)
        {
            ShowLibraryError("Could not restore the original: " + ex.Message);
        }
    }

    private async Task KeepEditsAsync(SourceCopyStatus copy)
    {
        try
        {
            await Task.Run(() => IndicatorSourceCopies.KeepEdits(copy.Directory));
            await _svc.ReviewSourceCopiesAsync();
        }
        catch (Exception ex)
        {
            ShowLibraryError("Could not update the copy record: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ drag to reorder

    private void Draggable(ListBoxItem item, IndicatorInfo info)
    {
        item.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed) return;
            _dragFrom = info;
            _dragStart = e.GetPosition(_available);
            _dragging = false;
        };
        item.PointerMoved += (_, e) =>
        {
            if (_dragFrom == null || !e.GetCurrentPoint(item).Properties.IsLeftButtonPressed) return;
            var p = e.GetPosition(_available);
            if (!_dragging && Math.Abs(p.Y - _dragStart.Y) + Math.Abs(p.X - _dragStart.X) < 7) return;
            _dragging = true;
            ShowDrop(TargetAt(p));
        };
        item.PointerReleased += (_, e) =>
        {
            var from = _dragFrom;
            bool dragging = _dragging;
            _dragFrom = null;
            _dragging = false;
            ShowDrop(null);
            if (from == null || !dragging) return;
            e.Handled = true;
            Drop(from, TargetAt(e.GetPosition(_available)));
        };
    }

    private ListBoxItem? TargetAt(Point p)
    {
        var hit = _available.InputHitTest(p) as Visual;
        return hit as ListBoxItem ?? hit?.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
    }

    private void ShowDrop(ListBoxItem? target)
    {
        if (ReferenceEquals(_dropOn, target)) return;
        if (_dropOn != null)
        {
            _dropOn.BorderThickness = new Thickness(0);
            _dropOn.BorderBrush = null;
        }

        _dropOn = target;
        if (target == null) return;
        target.BorderBrush = Ui.Accent;
        target.BorderThickness = new Thickness(0, 2, 0, 0); // dropped before this row
    }

    private void Drop(IndicatorInfo from, ListBoxItem? target)
    {
        var all = _svc.Catalog.Items;
        string type = from.Type.FullName!;
        switch (target?.Tag)
        {
            case IndicatorInfo before when !Equals(before, from):
                var group = IndicatorLibrary.Build(all, Layout).FirstOrDefault(x => x.Items.Contains(before));
                if (group == null) return;
                IndicatorLibrary.Move(Layout, all, type, group.Id, before.Type.FullName);
                break;
            case IndicatorLibraryGroup g:
                IndicatorLibrary.Move(Layout, all, type, g.Id, null);
                break;
            default:
                return;
        }

        LibraryChanged(from);
    }
}
