using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NAITool.Services;

namespace NAITool;

public sealed partial class MainWindow
{
    private readonly HistoryFavoritesService _historyFavorites = new(
        OutputBaseDir, Path.Combine(AppRootDir, "user", "config", "favorites.json"));
    private readonly HashSet<Button> _historyFavoriteButtons = [];
    private readonly HashSet<Border> _historyHoveredThumbnailHosts = [];
    private readonly Dictionary<string, bool> _historyFavoriteChanges = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlySet<string>? _historyFavoriteSnapshot;
    private bool _historyFavoritesLoaded;
    private Task _historyFavoriteWritesCompleted = Task.CompletedTask;
    private bool _historyFavoritesClosePending;
    private bool IsShowingHistoryFavorites => _currentMode == AppMode.Gallery && ChkGalleryFavoritesOnly.IsChecked == true;

    private async Task LoadHistoryFavoritesAsync()
    {
        try
        {
            await _historyFavorites.LoadAsync();
            if (_historyClosed) return;
            _historyFavoritesLoaded = true;
            ChkGalleryFavoritesOnly.IsEnabled = true;
            RefreshHistoryFavoriteButtons();
        }
        catch (Exception ex)
        {
            if (!_historyClosed) TxtStatus.Text = Lf("common.load_failed", ex.Message);
        }
    }

    private void ReloadHistoryFavoriteSnapshot()
    {
        _historyFavoriteSnapshot = null;
        if (!IsShowingHistoryFavorites) return;
        var paths = _historyFavorites.CapturePaths().ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Honor a click even if the user reloads the view before its asynchronous save finishes.
        foreach (var change in _historyFavoriteChanges)
        {
            if (change.Value) paths.Add(change.Key);
            else paths.Remove(change.Key);
        }
        _historyFavoriteSnapshot = paths;
    }

    private void OnGalleryFavoritesOnlyChanged(object sender, RoutedEventArgs e)
    {
        ReloadHistoryFavoriteSnapshot();
        BuildHistoryFileList();
        RefreshHistoryPanel(resetScroll: true);
    }

    private bool IsHistoryFavorite(string path) => _historyFavoriteChanges.TryGetValue(path, out bool favorite)
        ? favorite : _historyFavorites.IsFavorite(path);

    private void OnHistoryFavoriteLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        _historyFavoriteButtons.Add(button);
        UpdateHistoryFavoriteButton(button);
    }

    private void OnHistoryFavoriteUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) _historyFavoriteButtons.Remove(button);
    }

    private void OnHistoryFavoriteDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is Button button) UpdateHistoryFavoriteButton(button);
    }

    private void OnHistoryFavoriteFocusChanged(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) UpdateHistoryFavoriteButton(button);
    }

    private void OnHistoryThumbnailPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border) return;
        _historyHoveredThumbnailHosts.Add(border);
        UpdateHistoryFavoriteHost(border);
    }

    private void OnHistoryThumbnailPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border) return;
        _historyHoveredThumbnailHosts.Remove(border);
        UpdateHistoryFavoriteHost(border);
    }

    private void OnHistoryThumbnailHostUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border border) _historyHoveredThumbnailHosts.Remove(border);
    }

    private void UpdateHistoryFavoriteHost(Border border)
    {
        if (border.Child is Grid grid && grid.Children.OfType<Button>().FirstOrDefault() is { } button)
            UpdateHistoryFavoriteButton(button);
    }

    private void RefreshHistoryFavoriteButtons()
    {
        // Only realized thumbnails are visited, independent of the catalog's total size.
        foreach (var button in _historyFavoriteButtons) UpdateHistoryFavoriteButton(button);
    }

    private void UpdateHistoryFavoriteButton(Button button)
    {
        var path = (button.DataContext as HistoryListItem)?.FilePath;
        bool favorite = path != null && IsHistoryFavorite(path);
        bool hovered = button.Parent is Grid { Parent: Border host } && _historyHoveredThumbnailHosts.Contains(host);
        bool visible = path != null && (favorite || hovered || button.FocusState != FocusState.Unfocused);
        button.Visibility = path == null ? Visibility.Collapsed : Visibility.Visible;
        button.Opacity = visible ? 1 : 0;
        button.IsHitTestVisible = visible;
        button.IsEnabled = _historyFavoritesLoaded && path != null && !_historyFavoriteChanges.ContainsKey(path);
        if (button.Content is FontIcon icon) icon.Glyph = favorite ? "\uE735" : "\uE734";
        string label = L(favorite ? "history.unfavorite" : "history.favorite");
        ToolTipService.SetToolTip(button, label);
        AutomationProperties.SetName(button, label);
    }

    private async void OnHistoryFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: HistoryListItem { FilePath: { } path } })
            await ToggleHistoryFavoriteAsync(path);
    }

    private Task ToggleHistoryFavoriteAsync(string path)
    {
        if (!_historyFavoritesLoaded || !_historyFavorites.CanFavorite(path) || _historyFavoriteChanges.ContainsKey(path))
            return Task.CompletedTask;
        bool favorite = !_historyFavorites.IsFavorite(path);
        _historyFavoriteChanges[path] = favorite;
        RefreshHistoryFavoriteButtons();
        var save = SaveHistoryFavoriteChangeAsync(path, favorite);
        _historyFavoriteWritesCompleted = _historyFavoriteWritesCompleted.IsCompleted
            ? save : Task.WhenAll(_historyFavoriteWritesCompleted, save);
        return save;
    }

    private async Task SaveHistoryFavoriteChangeAsync(string path, bool favorite)
    {
        try { await _historyFavorites.SetFavoriteAsync(path, favorite); }
        catch (Exception ex)
        {
            if (!_historyClosed) TxtStatus.Text = Lf("common.save_failed", ex.Message);
        }
        finally
        {
            _historyFavoriteChanges.Remove(path);
            if (!_historyClosed) RefreshHistoryFavoriteButtons();
        }
        // Do not rebuild the active favorites snapshot: unstarred images stay until the next load.
    }

    private async Task CloseAfterHistoryFavoriteWritesAsync()
    {
        if (_historyFavoritesClosePending) return;
        _historyFavoritesClosePending = true;
        try
        {
            while (!_historyFavoriteWritesCompleted.IsCompleted)
                await _historyFavoriteWritesCompleted;
            Close();
        }
        finally { _historyFavoritesClosePending = false; }
    }

    private MenuFlyoutItem BuildHistoryFavoriteMenuItem(string? path)
    {
        bool favorite = path != null && IsHistoryFavorite(path);
        var item = new MenuFlyoutItem
        {
            Text = L(favorite ? "history.unfavorite" : "history.favorite"),
            Icon = new FontIcon
            {
                Style = (Style)RootGrid.Resources["HistoryFavoriteIconStyle"],
                Glyph = favorite ? "\uE735" : "\uE734",
            },
            IsEnabled = _historyFavoritesLoaded && path != null && _historyFavorites.CanFavorite(path) &&
                !_historyFavoriteChanges.ContainsKey(path),
        };
        item.Click += async (_, _) =>
        {
            if (path != null) await ToggleHistoryFavoriteAsync(path);
        };
        return item;
    }
}
