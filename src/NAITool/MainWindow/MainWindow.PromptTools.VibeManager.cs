using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NAITool.Services;
using Windows.Storage.Pickers;

namespace NAITool;

public sealed partial class MainWindow
{
    private bool _isVibeManagerDialogOpen;

    /// <summary>
    /// 氛围预编码管理器需要在关闭主对话框后执行的下一步动作。
    /// WinUI 3 同一时刻只能显示一个 ContentDialog，因此凡是需要唤起子对话框的按钮
    /// 都只登记此动作并调用 dialog.Hide()，外层循环关闭后统一派发。
    /// </summary>
    private enum VibeManagerPendingKind
    {
        None,
        AddNew,
        GroupReencode,
        RowReencode,
        DeleteEntryConfirm,
        DeleteGroupConfirm,
    }

    private sealed class VibeManagerPendingAction
    {
        public VibeManagerPendingKind Kind { get; set; } = VibeManagerPendingKind.None;
        public string? ImageHash { get; set; }
        public VibeCacheService.VibeCacheLookupEntry? Entry { get; set; }
    }

    /// <summary>
    /// 展示氛围预编码管理器主窗口。左栏按原图分组，右栏展示选中组的详情与子编码列表。
    /// </summary>
    private async void ShowVibeManagerDialog()
    {
        if (_isVibeManagerDialogOpen) return;
        _isVibeManagerDialogOpen = true;

        try
        {
            string cacheDir = VibeCacheService.GetCacheDir(AppRootDir);
            Directory.CreateDirectory(cacheDir);

            string? selectedImageHash = null;
            string initialStatus = "";

            while (true)
            {
                var pending = new VibeManagerPendingAction();
                var openResult = await ShowVibeManagerOnceAsync(
                    cacheDir, selectedImageHash, initialStatus, pending);
                selectedImageHash = openResult.SelectedImageHash;

                if (pending.Kind == VibeManagerPendingKind.None)
                    return;

                initialStatus = await DispatchVibeManagerPendingAsync(cacheDir, pending, s =>
                {
                    if (pending.Kind == VibeManagerPendingKind.DeleteGroupConfirm && s)
                        selectedImageHash = null;
                });
            }
        }
        finally
        {
            _isVibeManagerDialogOpen = false;
        }
    }

    /// <summary>
    /// 处理管理器关闭后的待执行动作（唤起子对话框），返回下次打开管理器时需要显示的状态文本。
    /// </summary>
    private async Task<string> DispatchVibeManagerPendingAsync(
        string cacheDir,
        VibeManagerPendingAction pending,
        Action<bool> onGroupDeleted)
    {
        switch (pending.Kind)
        {
            case VibeManagerPendingKind.AddNew:
            {
                bool did = await ShowVibeEncodeDialog(null);
                return did ? L("dialog.vibe_manager.status.entry_added") : "";
            }
            case VibeManagerPendingKind.GroupReencode:
            case VibeManagerPendingKind.RowReencode:
            {
                if (!string.IsNullOrWhiteSpace(pending.ImageHash) && pending.Entry != null)
                    await StartReencodeFromGroupAsync(cacheDir, pending.ImageHash!, pending.Entry);
                return "";
            }
            case VibeManagerPendingKind.DeleteEntryConfirm:
            {
                if (pending.Entry == null) return "";
                var confirmDlg = new ContentDialog
                {
                    Title = L("dialog.vibe_manager.action.delete_entry"),
                    Content = L("dialog.vibe_manager.action.delete_entry_confirm"),
                    PrimaryButtonText = L("common.ok"),
                    CloseButtonText = L("common.cancel"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.Content.XamlRoot,
                    RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
                };
                if (await confirmDlg.ShowAsync() != ContentDialogResult.Primary) return "";
                return VibeCacheService.DeleteEntry(cacheDir, pending.Entry, deletePhysicalFile: true)
                    ? L("dialog.vibe_manager.status.entry_deleted")
                    : "";
            }
            case VibeManagerPendingKind.DeleteGroupConfirm:
            {
                if (string.IsNullOrWhiteSpace(pending.ImageHash)) return "";
                var confirmDlg = new ContentDialog
                {
                    Title = L("dialog.vibe_manager.action.delete_group"),
                    Content = L("dialog.vibe_manager.action.delete_group_confirm"),
                    PrimaryButtonText = L("common.ok"),
                    CloseButtonText = L("common.cancel"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.Content.XamlRoot,
                    RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
                };
                if (await confirmDlg.ShowAsync() != ContentDialogResult.Primary) return "";
                bool deleted = VibeCacheService.DeleteGroup(cacheDir, pending.ImageHash!, deletePhysicalFiles: true);
                onGroupDeleted(deleted);
                return deleted ? L("dialog.vibe_manager.status.group_deleted") : "";
            }
            default:
                return "";
        }
    }

    private sealed class VibeManagerOpenResult
    {
        public string? SelectedImageHash { get; set; }
    }

    /// <summary>
    /// 构建并展示一次管理器 ContentDialog。
    /// 按钮需要唤起子对话框时，写入 pending 并调用 dialog.Hide()，由外层循环派发。
    /// </summary>
    private async Task<VibeManagerOpenResult> ShowVibeManagerOnceAsync(
        string cacheDir,
        string? initialSelectedHash,
        string initialStatus,
        VibeManagerPendingAction pending)
    {
        string? selectedImageHash = initialSelectedHash;

        var groupList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection(),
            MinWidth = 300,
        };

        var detailPanel = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(14, 4, 4, 4),
        };

        var statusBlock = new TextBlock
        {
            Text = initialStatus ?? "",
            TextWrapping = TextWrapping.WrapWholeWords,
            Opacity = 0.75,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var groupEntriesMap = new Dictionary<ListViewItem, string>();

        ContentDialog? dialogRef = null;

        void RequestAndHide(VibeManagerPendingKind kind, string? imageHash = null, VibeCacheService.VibeCacheLookupEntry? entry = null)
        {
            pending.Kind = kind;
            pending.ImageHash = imageHash;
            pending.Entry = entry;
            dialogRef?.Hide();
        }

        void RefreshDetailPanel()
        {
            detailPanel.Children.Clear();

            if (string.IsNullOrWhiteSpace(selectedImageHash))
            {
                detailPanel.Children.Add(new TextBlock
                {
                    Text = L("dialog.vibe_manager.detail.no_selection"),
                    Opacity = 0.6,
                    TextWrapping = TextWrapping.WrapWholeWords,
                });
                return;
            }

            var allEntries = VibeCacheService.ListAllEntries(cacheDir);
            var groupEntries = allEntries
                .Where(x => string.Equals(x.ImageHash, selectedImageHash, StringComparison.Ordinal))
                .ToList();

            if (groupEntries.Count == 0)
            {
                detailPanel.Children.Add(new TextBlock
                {
                    Text = L("dialog.vibe_manager.detail.group_removed"),
                    Opacity = 0.6,
                    TextWrapping = TextWrapping.WrapWholeWords,
                });
                return;
            }

            var first = groupEntries[0];
            string? originalPath = groupEntries
                .Select(x => x.OriginalImagePath)
                .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));

            bool thumbExists = VibeCacheService.ThumbnailExists(cacheDir, first);
            bool originalRecorded = !string.IsNullOrWhiteSpace(originalPath);
            bool originalOnDisk = originalRecorded && File.Exists(originalPath!);

            var topGrid = new Grid { ColumnSpacing = 12 };
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var bigThumb = new Image
            {
                Width = 128,
                Height = 128,
                Stretch = Stretch.UniformToFill,
            };
            var bigThumbBorder = new Border
            {
                Width = 128,
                Height = 128,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 128, 128, 128)),
                Child = bigThumb,
                VerticalAlignment = VerticalAlignment.Top,
            };
            bigThumbBorder.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, 128, 128),
            };

            if (thumbExists)
            {
                string? thumbPath = VibeCacheService.GetThumbnailPath(
                    cacheDir, first.ImageHash, first.ThumbnailHash);
                if (thumbPath != null)
                {
                    try { bigThumb.Source = new BitmapImage(new Uri(thumbPath)); }
                    catch { }
                }
            }

            Grid.SetColumn(bigThumbBorder, 0);
            topGrid.Children.Add(bigThumbBorder);

            var headerRight = new StackPanel { Spacing = 4 };

            headerRight.Children.Add(new TextBlock
            {
                Text = L("dialog.vibe_manager.detail.original_path"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.85,
                FontSize = 13,
            });

            string originalPathText = originalRecorded
                ? originalPath!
                : L("dialog.vibe_manager.detail.original_path_unknown");

            headerRight.Children.Add(new TextBlock
            {
                Text = originalPathText,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = originalRecorded ? 0.9 : 0.55,
                IsTextSelectionEnabled = true,
            });

            var tripleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 4, 0, 0),
            };
            tripleRow.Children.Add(BuildTripleStateIndicator(
                L("dialog.vibe_manager.detail.state_original"),
                originalRecorded
                    ? (originalOnDisk ? TripleState.Ok : TripleState.Missing)
                    : TripleState.Unknown));
            tripleRow.Children.Add(BuildTripleStateIndicator(
                L("dialog.vibe_manager.detail.state_thumbnail"),
                thumbExists ? TripleState.Ok : TripleState.Missing));
            int totalCount = groupEntries.Count;
            int missingVibeCount = groupEntries.Count(e => !VibeCacheService.VibeFileExists(cacheDir, e));
            tripleRow.Children.Add(BuildTripleStateIndicator(
                Lf("dialog.vibe_manager.detail.state_encodings", totalCount - missingVibeCount, totalCount),
                missingVibeCount == 0 ? TripleState.Ok : (missingVibeCount < totalCount ? TripleState.Partial : TripleState.Missing)));

            headerRight.Children.Add(tripleRow);

            var headerActionRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 6, 0, 0),
            };

            var relocateBtn = new Button
            {
                Content = L("dialog.vibe_manager.detail.relocate_original"),
                FontSize = 12,
            };
            relocateBtn.Click += async (_, _) =>
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".webp");
                picker.FileTypeFilter.Add(".bmp");
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(this));

                var file = await picker.PickSingleFileAsync();
                if (file == null) return;

                byte[]? pngBytes = await ReadImageFileAsPngAsync(file);
                if (pngBytes == null)
                {
                    statusBlock.Text = Lf("dialog.vibe_encode.read_failed", file.Name);
                    return;
                }

                string newHash = VibeCacheService.ComputeImageHash(pngBytes);
                if (!string.Equals(newHash, selectedImageHash, StringComparison.Ordinal))
                {
                    statusBlock.Text = Lf("dialog.vibe_manager.status.relocate_hash_mismatch",
                        selectedImageHash ?? "", newHash);
                    return;
                }

                if (VibeCacheService.UpdateOriginalPath(cacheDir, selectedImageHash!, file.Path))
                {
                    statusBlock.Text = Lf("dialog.vibe_manager.status.relocated", file.Path);
                    RefreshDetailPanel();
                    RefreshGroupList();
                }
            };
            headerActionRow.Children.Add(relocateBtn);

            var reencodeGroupBtn = new Button
            {
                Content = L("dialog.vibe_manager.action.reencode"),
                FontSize = 12,
            };
            string capturedHash = selectedImageHash!;
            var capturedFirst = first;
            reencodeGroupBtn.Click += (_, _) =>
            {
                RequestAndHide(VibeManagerPendingKind.GroupReencode, capturedHash, capturedFirst);
            };
            headerActionRow.Children.Add(reencodeGroupBtn);

            var deleteGroupBtn = new Button
            {
                Content = L("dialog.vibe_manager.action.delete_group"),
                FontSize = 12,
            };
            deleteGroupBtn.Click += (_, _) =>
            {
                RequestAndHide(VibeManagerPendingKind.DeleteGroupConfirm, capturedHash);
            };
            headerActionRow.Children.Add(deleteGroupBtn);

            headerRight.Children.Add(headerActionRow);

            Grid.SetColumn(headerRight, 1);
            topGrid.Children.Add(headerRight);

            detailPanel.Children.Add(topGrid);

            detailPanel.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 8, 0, 4),
                Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            });

            detailPanel.Children.Add(new TextBlock
            {
                Text = Lf("dialog.vibe_manager.detail.encodings_header", groupEntries.Count),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.85,
                FontSize = 13,
            });

            foreach (var entry in groupEntries.OrderBy(e => e.Model, StringComparer.OrdinalIgnoreCase)
                                               .ThenBy(e => e.InformationExtractedKey, StringComparer.Ordinal))
            {
                detailPanel.Children.Add(BuildEncodingRow(
                    cacheDir, entry, statusBlock, RequestAndHide));
            }
        }

        void RefreshGroupList()
        {
            groupList.Items.Clear();
            groupEntriesMap.Clear();

            var groups = VibeCacheService.GroupEntriesByImage(cacheDir);
            if (groups.Count == 0)
            {
                var emptyItem = new ListViewItem
                {
                    Content = new TextBlock
                    {
                        Text = L("dialog.vibe_manager.group.empty"),
                        Opacity = 0.55,
                        Margin = new Thickness(8),
                    },
                    IsHitTestVisible = false,
                };
                groupList.Items.Add(emptyItem);
                return;
            }

            foreach (var group in groups)
            {
                var item = BuildGroupListItem(cacheDir, group.Key, group.ToList());
                groupEntriesMap[item] = group.Key;
                groupList.Items.Add(item);

                if (string.Equals(selectedImageHash, group.Key, StringComparison.Ordinal))
                    groupList.SelectedItem = item;
            }
        }

        groupList.SelectionChanged += (_, _) =>
        {
            if (groupList.SelectedItem is ListViewItem lvi &&
                groupEntriesMap.TryGetValue(lvi, out var hash))
            {
                selectedImageHash = hash;
                RefreshDetailPanel();
            }
        };

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var addBtn = new Button { Content = L("dialog.vibe_manager.action.add_encode") };
        addBtn.Click += (_, _) => RequestAndHide(VibeManagerPendingKind.AddNew);
        toolbar.Children.Add(addBtn);

        var pruneBtn = new Button { Content = L("dialog.vibe_manager.action.prune_missing") };
        pruneBtn.Click += (_, _) =>
        {
            int removed = VibeCacheService.PruneMissingEntries(cacheDir);
            statusBlock.Text = Lf("dialog.vibe_manager.status.pruned", removed);
            if (removed > 0)
            {
                RefreshGroupList();
                RefreshDetailPanel();
            }
        };
        toolbar.Children.Add(pruneBtn);

        var refreshBtn = new Button { Content = L("dialog.vibe_manager.action.refresh") };
        refreshBtn.Click += (_, _) =>
        {
            RefreshGroupList();
            RefreshDetailPanel();
        };
        toolbar.Children.Add(refreshBtn);

        var body = new Grid { ColumnSpacing = 10 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var leftScroll = new ScrollViewer
        {
            Content = groupList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = 520,
        };
        Grid.SetColumn(leftScroll, 0);
        body.Children.Add(leftScroll);

        var rightScroll = new ScrollViewer
        {
            Content = detailPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = 520,
        };
        Grid.SetColumn(rightScroll, 1);
        body.Children.Add(rightScroll);

        var root = new StackPanel { Spacing = 4, MinWidth = 820 };
        root.Children.Add(toolbar);
        root.Children.Add(body);
        root.Children.Add(statusBlock);

        var dialog = new ContentDialog
        {
            Title = L("dialog.vibe_manager.title"),
            Content = root,
            CloseButtonText = L("button.close"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 900.0;
        dialogRef = dialog;

        RefreshGroupList();
        RefreshDetailPanel();

        await dialog.ShowAsync();

        return new VibeManagerOpenResult { SelectedImageHash = selectedImageHash };
    }

    private enum TripleState
    {
        Ok,
        Partial,
        Missing,
        Unknown,
    }

    private UIElement BuildTripleStateIndicator(string label, TripleState state)
    {
        var (glyph, color, opacity) = state switch
        {
            TripleState.Ok => ("\uE930", Windows.UI.Color.FromArgb(255, 80, 200, 120), 1.0),
            TripleState.Partial => ("\uE7BA", Windows.UI.Color.FromArgb(255, 220, 160, 60), 1.0),
            TripleState.Missing => ("\uE783", Windows.UI.Color.FromArgb(255, 220, 80, 80), 1.0),
            _ => ("\uE9CE", Windows.UI.Color.FromArgb(255, 160, 160, 160), 0.75),
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
        };
        panel.Children.Add(new FontIcon
        {
            FontFamily = SymbolFontFamily,
            Glyph = glyph,
            FontSize = 13,
            Foreground = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Opacity = opacity,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return panel;
    }

    private ListViewItem BuildGroupListItem(
        string cacheDir,
        string imageHash,
        List<VibeCacheService.VibeCacheLookupEntry> entries)
    {
        var first = entries[0];
        string? originalPath = entries
            .Select(x => x.OriginalImagePath)
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        bool thumbExists = VibeCacheService.ThumbnailExists(cacheDir, first);
        bool originalRecorded = !string.IsNullOrWhiteSpace(originalPath);
        bool originalOnDisk = originalRecorded && File.Exists(originalPath!);
        bool anyVibeMissing = entries.Any(e => !VibeCacheService.VibeFileExists(cacheDir, e));
        bool anyMissing = !thumbExists || (originalRecorded && !originalOnDisk) || anyVibeMissing;

        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var thumb = new Image
        {
            Width = 56,
            Height = 56,
            Stretch = Stretch.UniformToFill,
        };
        var thumbBorder = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 128, 128, 128)),
            Child = thumb,
        };
        thumbBorder.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, 56, 56),
        };

        if (thumbExists)
        {
            string? thumbPath = VibeCacheService.GetThumbnailPath(cacheDir, first.ImageHash, first.ThumbnailHash);
            if (thumbPath != null)
            {
                try { thumb.Source = new BitmapImage(new Uri(thumbPath)); }
                catch { }
            }
        }

        Grid.SetColumn(thumbBorder, 0);
        grid.Children.Add(thumbBorder);

        string displayName;
        if (originalRecorded)
        {
            try { displayName = Path.GetFileName(originalPath!); }
            catch { displayName = originalPath!; }
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = originalPath!;
        }
        else
        {
            string shortHash = imageHash.Length > 8 ? imageHash.Substring(0, 8) : imageHash;
            displayName = Lf("dialog.vibe_manager.group.hash_fallback", shortHash);
        }

        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = displayName,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        });
        info.Children.Add(new TextBlock
        {
            Text = Lf("dialog.vibe_manager.group.count", entries.Count),
            FontSize = 11,
            Opacity = 0.7,
        });
        if (!originalRecorded)
        {
            info.Children.Add(new TextBlock
            {
                Text = L("dialog.vibe_manager.group.no_original_recorded"),
                FontSize = 10,
                Opacity = 0.55,
            });
        }

        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        if (anyMissing)
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 80, 80)),
                CornerRadius = new CornerRadius(8),
                Width = 10,
                Height = 10,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 4, 0),
            };
            ToolTipService.SetToolTip(badge, L("dialog.vibe_manager.group.missing_badge"));
            Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);
        }

        return new ListViewItem
        {
            Content = grid,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
    }

    private UIElement BuildEncodingRow(
        string cacheDir,
        VibeCacheService.VibeCacheLookupEntry entry,
        TextBlock statusBlock,
        Action<VibeManagerPendingKind, string?, VibeCacheService.VibeCacheLookupEntry?> requestAndHide)
    {
        bool vibeExists = VibeCacheService.VibeFileExists(cacheDir, entry);

        var grid = new Grid
        {
            ColumnSpacing = 10,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 2, 0, 2),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            CornerRadius = new CornerRadius(4),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = entry.Model,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var metaRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
        };
        metaRow.Children.Add(new TextBlock
        {
            Text = Lf("dialog.vibe_manager.row.ie", entry.InformationExtractedKey),
            FontSize = 11,
            Opacity = 0.8,
        });
        if (!string.IsNullOrWhiteSpace(entry.CreatedAtUtc) &&
            DateTimeOffset.TryParse(entry.CreatedAtUtc, out var dt))
        {
            metaRow.Children.Add(new TextBlock
            {
                Text = Lf("dialog.vibe_manager.row.created_at", dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
                FontSize = 11,
                Opacity = 0.7,
            });
        }
        metaRow.Children.Add(new TextBlock
        {
            Text = vibeExists
                ? L("dialog.vibe_manager.row.vibe_ok")
                : L("dialog.vibe_manager.row.vibe_missing"),
            FontSize = 11,
            Foreground = new SolidColorBrush(vibeExists
                ? Windows.UI.Color.FromArgb(255, 80, 200, 120)
                : Windows.UI.Color.FromArgb(255, 220, 80, 80)),
            Opacity = 0.95,
        });
        info.Children.Add(metaRow);

        info.Children.Add(new TextBlock
        {
            Text = entry.VibeFileName,
            FontSize = 11,
            Opacity = 0.55,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = true,
        });

        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (!vibeExists)
        {
            var reencodeRowBtn = new Button
            {
                Content = L("dialog.vibe_manager.action.row_reencode"),
                FontSize = 12,
            };
            reencodeRowBtn.Click += (_, _) =>
                requestAndHide(VibeManagerPendingKind.RowReencode, entry.ImageHash, entry);
            actions.Children.Add(reencodeRowBtn);
        }

        var exportBtn = new Button
        {
            Content = L("dialog.vibe_manager.action.export_vibe"),
            FontSize = 12,
            IsEnabled = vibeExists,
        };
        exportBtn.Click += async (_, _) =>
        {
            await ExportVibeEntryAsync(cacheDir, entry, statusBlock);
        };
        actions.Children.Add(exportBtn);

        var delBtn = new Button
        {
            Content = L("dialog.vibe_manager.action.delete_entry"),
            FontSize = 12,
        };
        delBtn.Click += (_, _) =>
            requestAndHide(VibeManagerPendingKind.DeleteEntryConfirm, entry.ImageHash, entry);
        actions.Children.Add(delBtn);

        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        return grid;
    }

    /// <summary>
    /// 从分组数据启动"再编码"二级窗口：优先使用 OriginalImagePath 读取原图字节；失败时降级为缩略图；全部不可用时允许临时选图。
    /// </summary>
    private async Task StartReencodeFromGroupAsync(
        string cacheDir,
        string imageHash,
        VibeCacheService.VibeCacheLookupEntry sampleEntry)
    {
        byte[]? presetBytes = null;
        string? presetPath = null;
        string? presetFileName = null;
        bool fallbackToThumbnail = false;

        var allEntries = VibeCacheService.ListAllEntries(cacheDir);
        string? originalPath = allEntries
            .Where(x => string.Equals(x.ImageHash, imageHash, StringComparison.Ordinal))
            .Select(x => x.OriginalImagePath)
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));

        if (!string.IsNullOrWhiteSpace(originalPath) && File.Exists(originalPath))
        {
            try
            {
                byte[] raw = await File.ReadAllBytesAsync(originalPath!);
                using var skBitmap = SkiaSharp.SKBitmap.Decode(raw);
                if (skBitmap != null)
                {
                    using var img = SkiaSharp.SKImage.FromBitmap(skBitmap);
                    using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                    byte[]? png = data?.ToArray();
                    if (png != null && png.Length > 0)
                    {
                        string hashCheck = VibeCacheService.ComputeImageHash(png);
                        if (string.Equals(hashCheck, imageHash, StringComparison.Ordinal))
                        {
                            presetBytes = png;
                            presetPath = originalPath;
                            presetFileName = Path.GetFileName(originalPath!);
                        }
                    }
                }
            }
            catch { }
        }

        if (presetBytes == null)
        {
            string? thumbPath = VibeCacheService.GetThumbnailPath(cacheDir, sampleEntry.ImageHash, sampleEntry.ThumbnailHash);
            if (thumbPath != null && File.Exists(thumbPath))
            {
                try
                {
                    presetBytes = await File.ReadAllBytesAsync(thumbPath);
                    presetFileName = Path.GetFileName(thumbPath);
                    fallbackToThumbnail = true;
                }
                catch { }
            }
        }

        var context = new VibeEncodeContext(
            FixedImageHash: (presetBytes != null && !fallbackToThumbnail) ? imageHash : null,
            PresetImageBytes: presetBytes,
            PresetImagePath: presetPath,
            PresetFileName: presetFileName ?? Lf("dialog.vibe_manager.row.fallback_name", imageHash),
            OriginalMissingFallback: fallbackToThumbnail);

        await ShowVibeEncodeDialog(context);
    }

    private async Task ExportVibeEntryAsync(
        string cacheDir,
        VibeCacheService.VibeCacheLookupEntry entry,
        TextBlock statusBlock)
    {
        if (!VibeCacheService.VibeFileExists(cacheDir, entry))
        {
            statusBlock.Text = L("dialog.vibe_manager.status.export_missing");
            return;
        }

        var picker = new FileSavePicker();
        picker.SuggestedFileName = Path.GetFileNameWithoutExtension(entry.VibeFileName);
        picker.FileTypeChoices.Add("NovelAI Vibe", new List<string> { ".naiv4vibe" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(this));

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        try
        {
            string src = Path.Combine(cacheDir, entry.VibeFileName);
            byte[] bytes = await File.ReadAllBytesAsync(src);
            await File.WriteAllBytesAsync(file.Path, bytes);
            statusBlock.Text = Lf("dialog.vibe_manager.status.exported", file.Path);
        }
        catch (Exception ex)
        {
            statusBlock.Text = Lf("dialog.vibe_manager.status.export_failed", ex.Message);
        }
    }
}
