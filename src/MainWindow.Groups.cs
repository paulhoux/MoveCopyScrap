using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MoveCopyScrap.Models;
using MoveCopyScrap.Services;

namespace MoveCopyScrap;

public sealed partial class MainWindow
{
    private List<MarkGroup> _groups = new();
    private string _activeGroupId = "";
    private bool _dialogOpen;
    private readonly Dictionary<string, Border> _groupPills = new();
    private readonly Dictionary<string, (string Label, double Width)> _groupLabels = new();
    private static readonly Windows.UI.Color[] GroupColors =
    {
        Colors.Gold, Colors.MediumTurquoise, Colors.LightSkyBlue,
        Colors.LightCoral, Colors.Plum, Colors.YellowGreen
    };

    private static Windows.UI.Color GroupColor(MarkGroup group) =>
        GroupColors[((group.ColorIndex % GroupColors.Length) + GroupColors.Length) % GroupColors.Length];

    private static void ApplyGroup(MediaItem item, MarkGroup? group) =>
        item.SetGroup(group, group is null ? Colors.Gold : GroupColor(group));

    private void SaveGroups() => _markStore?.SaveGroups(_groups, _activeGroupId);

    private void UpdateGroupColors()
    {
        for (int index = 0; index < _groups.Count; index++)
            _groups[index].ColorIndex = index;
        foreach (var item in _items.Where(i => i.IsMarked))
            ApplyGroup(item, _groups.FirstOrDefault(g => g.Id == item.GroupId));
        SaveGroups();
    }

    private void SelectGroup(MarkGroup group)
    {
        if (_busy || _dialogOpen) return;
        _activeGroupId = group.Id;
        SaveGroups();
        UpdateCommandState();
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void OnAddGroupClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _dialogOpen || _markStore is null) return;
        SelectGroup(AddGroup());
        UpdateInsets();
    }

    private MarkGroup AddGroup()
    {
        int number = 1;
        while (_groups.Any(g => g.Name == $"Group {number}")) number++;
        var group = new MarkGroup { Name = $"Group {number}", ColorIndex = _groups.Count };
        _groups.Add(group);
        return group;
    }

    private void AssignCurrentToGroup(int index)
    {
        var item = CurrentItem;
        if (_busy || _dialogOpen || _markStore is null || item is null || index < 0 || index > 8) return;

        while (_groups.Count <= index) AddGroup();
        var group = _groups[index];
        _activeGroupId = group.Id;
        ApplyGroup(item, group);
        SaveGroups();
        _markStore.Assign(item.FileName, group.Id);
        Carousel.RefreshMarkVisuals();
        UpdateCommandState();
        UpdateCaption();
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void RefreshGroups()
    {
        bool multiple = _groups.Count > 1;
        GroupsBar.Visibility = _groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GroupPills.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        GroupsBar.IsHitTestVisible = !_busy;
        AddGroupButton.IsEnabled = !_busy;
        OpenFolderButton.IsEnabled = !_busy;
        CopyButton.Visibility = MoveButton.Visibility = DeleteButton.Visibility =
            multiple ? Visibility.Collapsed : Visibility.Visible;
        OrganiseButton.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        OrganiseButton.IsEnabled = !_busy && _items.Any(i => i.IsMarked);
        MarkCountPill.Visibility = multiple ? Visibility.Collapsed : Visibility.Visible;

        foreach (var id in _groupPills.Keys.Where(id => !_groups.Any(g => g.Id == id)).ToList())
        {
            GroupPills.Children.Remove(_groupPills[id]);
            _groupPills.Remove(id);
            _groupLabels.Remove(id);
        }
        foreach (var group in _groups)
        {
            if (!_groupPills.TryGetValue(group.Id, out var ring))
            {
                var button = new Button
                {
                    Style = (Style)Application.Current.Resources["CommandButtonStyle"],
                    Padding = new Thickness(10, 5, 10, 5), MaxWidth = 220,
                    Content = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis }
                };
                button.Click += (_, _) => SelectGroup(group);
                button.DoubleTapped += async (_, e) => { e.Handled = true; await RenameGroupAsync(group); };
                var menu = new MenuFlyout();
                var rename = new MenuFlyoutItem { Text = "Rename...", Icon = new SymbolIcon(Symbol.Edit) };
                rename.Click += async (_, _) => await RenameGroupAsync(group);
                var remove = new MenuFlyoutItem { Text = "Remove group...", Icon = new SymbolIcon(Symbol.Delete) };
                remove.Click += async (_, _) => await RemoveGroupAsync(group);
                menu.Items.Add(rename);
                menu.Items.Add(remove);
                button.ContextFlyout = menu;
                ring = new Border { BorderThickness = new Thickness(2), Padding = new Thickness(3),
                    CornerRadius = new CornerRadius(8), Child = button };
                _groupPills.Add(group.Id, ring);
                GroupPills.Children.Add(ring);
            }
            var color = GroupColor(group);
            ring.BorderBrush = new SolidColorBrush(group.Id == _activeGroupId ? color : Colors.Transparent);
            var pill = (Button)ring.Child;
            pill.IsEnabled = !_busy;
            pill.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(45, color.R, color.G, color.B));
            string label = $"{group.Name} * {_items.Count(i => i.GroupId == group.Id && i.IsMarked)}";
            var measure = new TextBlock { Text = label, FontSize = pill.FontSize, FontFamily = pill.FontFamily };
            measure.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            _groupLabels[group.Id] = (label, Math.Clamp(measure.DesiredSize.Width + 24, 36, 220));
            ToolTipService.SetToolTip(pill, $"{label} - double-click to rename");
            AutomationProperties.SetName(pill, label + (group.Id == _activeGroupId ? ", active group" : ""));
        }
        UpdateGroupLabelLayout();
    }

    private void UpdateGroupLabelLayout()
    {
        if (_groups.Count < 2 || CommandScroller.ActualWidth <= 0) return;
        // Compare against full labels even while compact, so layout cannot oscillate
        // between the two modes as the pills shrink and grow.
        double fullGroupWidth = _groupLabels.Values.Sum(entry => entry.Width + 10) +
            Math.Max(0, _groups.Count - 1) * GroupPills.Spacing + GroupsBar.Spacing + AddGroupButton.DesiredSize.Width;
        var others = CommandItems.Children.Where(child => child.Visibility == Visibility.Visible && child != GroupsBar).ToList();
        double fullWidth = others.Sum(child => child.DesiredSize.Width) +
            others.Count * CommandItems.Spacing + fullGroupWidth;
        bool compact = fullWidth > CommandScroller.ActualWidth;
        for (int index = 0; index < _groups.Count; index++)
        {
            var id = _groups[index].Id;
            if (!_groupLabels.TryGetValue(id, out var entry) || !_groupPills.TryGetValue(id, out var ring)) continue;
            var pill = (Button)ring.Child;
            var text = (TextBlock)pill.Content;
            string label = compact ? (index + 1).ToString() : entry.Label;
            double width = compact ? 36 : entry.Width;
            if (text.Text != label) text.Text = label;
            if (pill.Width != width) pill.Width = width;
        }
    }

    private async Task RenameGroupAsync(MarkGroup group)
    {
        if (_dialogOpen || _busy) return;
        _dialogOpen = true;
        try
        {
            var name = new TextBox { Text = group.Name, MaxLength = 60, Header = "Group name" };
            name.Loaded += (_, _) => { name.Focus(FocusState.Programmatic); name.SelectAll(); };
            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "Rename group",
                Content = name, PrimaryButtonText = "Save", CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            group.Name = string.IsNullOrWhiteSpace(name.Text) ? $"Group {_groups.IndexOf(group) + 1}" : name.Text.Trim();
            foreach (var item in _items.Where(i => i.GroupId == group.Id)) ApplyGroup(item, group);
            SaveGroups();
            UpdateCommandState();
            UpdateCaption();
        }
        finally { _dialogOpen = false; RootGrid.Focus(FocusState.Programmatic); }
    }

    private async Task RemoveGroupAsync(MarkGroup group)
    {
        if (_dialogOpen || _busy) return;
        if (_groups.Count == 1)
        {
            ShowStatus(InfoBarSeverity.Informational, "Keep at least one group", "You can rename this group.");
            return;
        }
        int count = _items.Count(i => i.GroupId == group.Id);
        if (!await ConfirmAsync($"Remove {group.Name}?", $"{count} file(s) will be unmarked. No files will be deleted.", "Remove group")) return;
        foreach (var item in _items.Where(i => i.GroupId == group.Id))
        {
            ApplyGroup(item, null);
            _markStore?.Assign(item.FileName, null);
        }
        _groups.Remove(group);
        if (_activeGroupId == group.Id) _activeGroupId = _groups[0].Id;
        UpdateGroupColors();
        Carousel.RefreshMarkVisuals();
        UpdateCommandState();
        UpdateCaption();
    }

    private async void OnOrganiseClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _dialogOpen || _markStore is null || !_items.Any(i => i.IsMarked)) return;
        _dialogOpen = true;
        try
        {
            var rows = new StackPanel { Spacing = 12, MinWidth = 420 };
            var error = new TextBlock { Foreground = new SolidColorBrush(Colors.LightCoral), TextWrapping = TextWrapping.Wrap };
            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "Organise groups",
                PrimaryButtonText = "Organise", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
            foreach (var group in _groups)
            {
                if (group.Action is not ("Skip" or "Copy" or "Move" or "Delete")) group.Action = "Skip";
                var row = new Grid { ColumnSpacing = 12, RowSpacing = 6 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                int count = _items.Count(i => i.GroupId == group.Id);
                row.Children.Add(new TextBlock { Text = $"{group.Name} * {count}", VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(GroupColor(group)) });
                var action = new ComboBox { ItemsSource = new[] { "Skip", "Copy", "Move", "Delete" },
                    SelectedItem = group.Action, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = count > 0 };
                AutomationProperties.SetName(action, $"Action for {group.Name}");
                Grid.SetColumn(action, 1);
                row.Children.Add(action);
                var destinationRow = new Grid { ColumnSpacing = 6 };
                destinationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                destinationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var destination = new TextBox { Text = group.Destination, PlaceholderText = "Destination folder" };
                AutomationProperties.SetName(destination, $"Destination for {group.Name}");
                var browse = new Button { Content = new FontIcon { Glyph = "\uE838", FontSize = 15 } };
                ToolTipService.SetToolTip(browse, "Choose destination folder");
                Grid.SetColumn(browse, 1);
                destinationRow.Children.Add(destination);
                destinationRow.Children.Add(browse);
                Grid.SetRow(destinationRow, 1);
                Grid.SetColumnSpan(destinationRow, 2);
                row.Children.Add(destinationRow);
                void UpdateDestination() => destinationRow.Visibility = group.Action is "Copy" or "Move" ? Visibility.Visible : Visibility.Collapsed;
                action.SelectionChanged += (_, _) => { group.Action = action.SelectedItem as string ?? "Skip"; UpdateDestination(); SaveGroups(); };
                destination.TextChanged += (_, _) => { group.Destination = destination.Text.Trim(); SaveGroups(); };
                browse.Click += async (_, _) =>
                {
                    dialog.IsPrimaryButtonEnabled = false;
                    try { var folder = await PickFolderAsync(); if (folder is not null) destination.Text = folder; }
                    finally { dialog.IsPrimaryButtonEnabled = true; }
                };
                UpdateDestination();
                rows.Children.Add(row);
            }
            rows.Children.Add(error);
            dialog.Content = new ScrollViewer { Content = rows, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                var planned = _groups.Where(g => g.Action != "Skip" && _items.Any(i => i.GroupId == g.Id)).ToList();
                error.Text = "";
                if (planned.Count == 0) error.Text = "Choose an action for at least one non-empty group.";
                foreach (var group in planned.Where(g => g.Action is "Copy" or "Move"))
                {
                    try
                    {
                        if (!Path.IsPathFullyQualified(group.Destination)) throw new ArgumentException();
                        string target = Path.GetFullPath(group.Destination).TrimEnd('\\', '/');
                        if (string.Equals(target, Path.GetFullPath(_folder!).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException();
                    }
                    catch { error.Text = $"Choose a destination outside the current folder for {group.Name}."; break; }
                }
                args.Cancel = error.Text.Length > 0;
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        finally { _dialogOpen = false; RootGrid.Focus(FocusState.Programmatic); }

        var plans = _groups.Where(g => g.Action != "Skip")
            .Select(g => (Group: g.Clone(), Items: _items.Where(i => i.GroupId == g.Id).ToList()))
            .Where(p => p.Items.Count > 0).ToList();
        bool recycle = FileOperations.SupportsRecycleBin(_folder!);
        string summary = string.Join("\n", plans.Select(p => $"{p.Group.Name}: {p.Group.Action} {p.Items.Count} file(s)" +
            (p.Group.Action is "Copy" or "Move" ? $" to {p.Group.Destination}" : "")));
        if (plans.Any(p => p.Group.Action == "Delete"))
            summary += recycle ? "\n\nDeleted files will go to the Recycle Bin." : "\n\nDeleted files will be removed PERMANENTLY. There is no Recycle Bin for this folder.";
        if (!await ConfirmAsync("Organise these groups?", summary, "Organise")) return;

        var aggregate = new FileOperationResult();
        ShowBusy("Organising...", 0);
        try
        {
            await FlushRotationsAsync();
            Carousel.ReleaseMedia();
            foreach (var plan in plans)
            {
                Carousel.ReleaseMedia();
                FileOperationResult result;
                try
                {
                    result = plan.Group.Action switch
                    {
                        "Copy" => await FileOperations.CopyAsync(plan.Items, plan.Group.Destination, CreateProgress()),
                        "Move" => await FileOperations.MoveAsync(plan.Items, plan.Group.Destination, CreateProgress()),
                        "Delete" => await FileOperations.DeleteAsync(plan.Items, recycle, CreateProgress()),
                        _ => throw new InvalidOperationException("Unknown group action.")
                    };
                }
                catch (Exception ex)
                {
                    foreach (var item in plan.Items) aggregate.Failures.Add((item.FileName, ex.Message));
                    continue;
                }
                aggregate.Succeeded += result.Succeeded;
                aggregate.Failures.AddRange(result.Failures);
                if (plan.Group.Action is "Move" or "Delete")
                    RemoveItems(result.Completed);
            }
            _markStore.Flush();
            Report("Organised", aggregate, null);
        }
        catch (Exception ex) { ShowStatus(InfoBarSeverity.Error, "Could not finish organising", ex.Message); }
        finally
        {
            Carousel.SetItems(_items, Math.Max(0, _currentIndex));
            HideBusy();
            UpdateCaption();
            RootGrid.Focus(FocusState.Programmatic);
        }
    }
}
