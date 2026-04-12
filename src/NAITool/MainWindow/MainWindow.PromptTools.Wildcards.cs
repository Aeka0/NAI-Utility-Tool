using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using NAITool.Controls;
using NAITool.Models;
using NAITool.Services;
using SkiaSharp;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using System.Runtime.InteropServices.WindowsRuntime;

namespace NAITool;

public sealed partial class MainWindow
{
    private async void ShowWildcardDialog()
    {
        if (_isWildcardDialogOpen) return;
        _isWildcardDialogOpen = true;

        try
        {
            string rootDir = GetWildcardsRootDir();
            Directory.CreateDirectory(rootDir);

            WildcardIndexEntry? selectedEntry = null;
            string currentRelativePath = "";

            var breadcrumbIcon = new FontIcon
            {
                FontFamily = SymbolFontFamily,
                Glyph = "\uEDA2",
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            var breadcrumb = new BreadcrumbBar();
            var breadcrumbItems = new System.Collections.ObjectModel.ObservableCollection<string>();
            breadcrumb.ItemsSource = breadcrumbItems;

            var breadcrumbRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };
            breadcrumbRow.Children.Add(breadcrumbIcon);
            breadcrumbRow.Children.Add(breadcrumb);

            var browserList = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection(),
            };

            var treeScroller = new ScrollViewer
            {
                Content = browserList,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };

            var editorBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 0,
                IsSpellCheckEnabled = false,
                IsEnabled = false,
                PlaceholderText = L("wildcards.editor_placeholder"),
            };
            ScrollViewer.SetVerticalScrollBarVisibility(editorBox, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(editorBox, ScrollBarVisibility.Auto);

            var metaBlock = new TextBlock
            {
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity = 0.8,
                FontSize = 12,
            };

            var statsBlock = new TextBlock
            {
                TextWrapping = TextWrapping.WrapWholeWords,
                FontSize = 12,
            };

            var listItemPathMap = new Dictionary<FrameworkElement, string>();
            var listItemEntryMap = new Dictionary<FrameworkElement, WildcardIndexEntry>();
            var expandedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            static string GetEntryDirectoryName(string entryName)
            {
                int slashIndex = entryName.LastIndexOf('/');
                return slashIndex >= 0 ? entryName[..slashIndex] : "";
            }

            FrameworkElement CreateListRow(string glyph, string label, string? detail, int depth, bool isBold)
            {
                var sp = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Padding = new Thickness(depth * 16, 4, 8, 4),
                };
                sp.Children.Add(new FontIcon
                {
                    FontFamily = SymbolFontFamily,
                    Glyph = glyph,
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                sp.Children.Add(new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = isBold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                });
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    sp.Children.Add(new TextBlock
                    {
                        Text = detail,
                        Opacity = 0.55,
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }
                return sp;
            }

            void UpdateBreadcrumbItems()
            {
                breadcrumbItems.Clear();
                breadcrumbItems.Add(L("wildcards.root"));
                if (!string.IsNullOrEmpty(currentRelativePath))
                {
                    foreach (string part in currentRelativePath.Split('/'))
                    {
                        if (!string.IsNullOrEmpty(part))
                            breadcrumbItems.Add(part);
                    }
                }
            }

            void UpdateDirectoryMeta(string? detail = null)
            {
                string dirLabel = string.IsNullOrEmpty(currentRelativePath) ? L("wildcards.root") : currentRelativePath.Replace('/', '/');
                metaBlock.Text = string.IsNullOrWhiteSpace(detail)
                    ? Lf("wildcards.current_directory", dirLabel)
                    : detail;
                statsBlock.Text = Lf("wildcards.stats", _wildcardService.FileCount, _wildcardService.OptionCount);
            }

            void SelectEntry(WildcardIndexEntry entry)
            {
                selectedEntry = entry;
                currentRelativePath = GetEntryDirectoryName(entry.Name);
                UpdateBreadcrumbItems();
                editorBox.IsEnabled = true;
                editorBox.Text = File.Exists(entry.FilePath)
                    ? File.ReadAllText(entry.FilePath, Encoding.UTF8) : "";
                UpdateDirectoryMeta(
                    Lf("wildcards.entry_meta", entry.Name, entry.OptionCount, entry.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")));
            }

            void ClearEntrySelectionForDirectory(string relativePath)
            {
                selectedEntry = null;
                currentRelativePath = relativePath;
                UpdateBreadcrumbItems();
                editorBox.IsEnabled = false;
                editorBox.Text = "";
                UpdateDirectoryMeta(L("wildcards.browse_or_select"));
            }

            void PopulateDirectoryRecursive(string relativePath, int depth)
            {
                bool isExpanded = expandedDirs.Contains(relativePath);

                foreach (string dir in _wildcardService.ListSubDirectories(relativePath))
                {
                    string childPath = string.IsNullOrEmpty(relativePath) ? dir : $"{relativePath}/{dir}";
                    bool childExpanded = expandedDirs.Contains(childPath);
                    string arrow = childExpanded ? "▾" : "▸";
                    var row = (FrameworkElement)CreateListRow("\uE8B7", $"{arrow} {dir}/", null, depth, isBold: true);
                    listItemPathMap[row] = childPath;
                    browserList.Items.Add(row);

                    if (childExpanded)
                        PopulateDirectoryRecursive(childPath, depth + 1);
                }

                foreach (var entry in _wildcardService.ListEntriesInDirectory(relativePath))
                {
                    string shortName = entry.Name.Contains('/')
                        ? entry.Name[(entry.Name.LastIndexOf('/') + 1)..]
                        : entry.Name;
                    var row = (FrameworkElement)CreateListRow("\uE8A5", shortName + ".txt", Lf("wildcards.option_count", entry.OptionCount), depth, isBold: false);
                    listItemEntryMap[row] = entry;
                    browserList.Items.Add(row);
                }
            }

            void RebuildBrowserTree(string? preferredEntryName = null, string? preferredDirectory = null, bool autoExpand = true)
            {
                listItemPathMap.Clear();
                listItemEntryMap.Clear();
                browserList.Items.Clear();

                if (autoExpand && !string.IsNullOrWhiteSpace(preferredDirectory) && !expandedDirs.Contains(preferredDirectory))
                {
                    var parts = preferredDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    string accum = "";
                    foreach (var part in parts)
                    {
                        accum = string.IsNullOrEmpty(accum) ? part : $"{accum}/{part}";
                        expandedDirs.Add(accum);
                    }
                }

                PopulateDirectoryRecursive("", 0);

                if (!string.IsNullOrWhiteSpace(preferredEntryName))
                {
                    foreach (var kvp in listItemEntryMap)
                    {
                        if (string.Equals(kvp.Value.Name, preferredEntryName, StringComparison.OrdinalIgnoreCase))
                        {
                            browserList.SelectedItem = kvp.Key;
                            SelectEntry(kvp.Value);
                            return;
                        }
                    }
                }
                ClearEntrySelectionForDirectory(preferredDirectory ?? currentRelativePath);
            }

            breadcrumb.ItemClicked += (_, args) =>
            {
                int clickedIndex = breadcrumbItems.IndexOf(args.Item?.ToString() ?? "");
                if (clickedIndex <= 0)
                {
                    RebuildBrowserTree(preferredDirectory: "");
                }
                else
                {
                    var parts = new List<string>();
                    for (int i = 1; i <= clickedIndex; i++)
                        parts.Add(breadcrumbItems[i]);
                    RebuildBrowserTree(preferredDirectory: string.Join("/", parts));
                }
            };

            browserList.SelectionChanged += (_, _) =>
            {
                if (browserList.SelectedItem is not FrameworkElement fe) return;

                if (listItemEntryMap.TryGetValue(fe, out var entry))
                {
                    SelectEntry(entry);
                }
                else if (listItemPathMap.TryGetValue(fe, out var dirPath))
                {
                    bool wasExpanded = expandedDirs.Contains(dirPath);
                    if (wasExpanded)
                        expandedDirs.Remove(dirPath);
                    else
                        expandedDirs.Add(dirPath);
                    RebuildBrowserTree(preferredDirectory: dirPath, autoExpand: !wasExpanded);
                }
            };

            var openBtn = new Button { Content = L("wildcards.open_folder"), MinWidth = 96 };
            openBtn.Click += (_, _) =>
            {
                string targetDir = string.IsNullOrEmpty(currentRelativePath)
                    ? rootDir : Path.Combine(rootDir, currentRelativePath.Replace('/', '\\'));
                Directory.CreateDirectory(targetDir);
                System.Diagnostics.Process.Start("explorer.exe", targetDir);
            };

            var reloadBtn = new Button { Content = L("wildcards.rescan"), MinWidth = 96 };
            reloadBtn.Click += (_, _) =>
            {
                LoadWildcards();
                RebuildBrowserTree(selectedEntry?.Name, currentRelativePath);
                TxtStatus.Text = L("wildcards.reloaded");
            };

            var saveBtn = new Button
            {
                Width = 32, Height = 32,
                Padding = new Thickness(0),
                Content = new FontIcon { FontFamily = SymbolFontFamily, Glyph = "\uE74E" },
            };
            ToolTipService.SetToolTip(saveBtn, L("menu.file.save"));
            saveBtn.Click += (_, _) =>
            {
                if (selectedEntry == null)
                {
                    TxtStatus.Text = L("wildcards.nothing_to_save");
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(selectedEntry.FilePath)!);
                File.WriteAllText(selectedEntry.FilePath, editorBox.Text ?? "", Encoding.UTF8);
                LoadWildcards();
                RebuildBrowserTree(selectedEntry.Name, currentRelativePath);
                TxtStatus.Text = Lf("wildcards.saved", selectedEntry.Name);
            };

            var rightPanel = new Grid { RowSpacing = 8 };
            rightPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rightPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var editorHeader = new Grid { ColumnSpacing = 8 };
            editorHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            editorHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var editorLabel = new TextBlock
            {
                Text = L("wildcards.entry_editor"),
                FontSize = 17,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(editorLabel, 0);
            Grid.SetColumn(saveBtn, 1);
            editorHeader.Children.Add(editorLabel);
            editorHeader.Children.Add(saveBtn);
            Grid.SetRow(editorHeader, 0);
            Grid.SetRow(editorBox, 1);
            rightPanel.Children.Add(editorHeader);
            rightPanel.Children.Add(editorBox);

            var bottomButtonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
            };
            bottomButtonRow.Children.Add(openBtn);
            bottomButtonRow.Children.Add(reloadBtn);

            var bodyGrid = new Grid
            {
                ColumnSpacing = 12,
                Height = 420,
            };
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240, GridUnitType.Pixel) });
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(treeScroller, 0);
            Grid.SetColumn(rightPanel, 1);
            bodyGrid.Children.Add(treeScroller);
            bodyGrid.Children.Add(rightPanel);

            var footerGrid = new Grid { ColumnSpacing = 12 };
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            metaBlock.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(statsBlock, 0);
            Grid.SetColumn(metaBlock, 1);
            footerGrid.Children.Add(statsBlock);
            footerGrid.Children.Add(metaBlock);

            var panel = new StackPanel
            {
                Spacing = 10,
                Width = 840,
            };
            panel.Children.Add(breadcrumbRow);
            panel.Children.Add(bodyGrid);
            panel.Children.Add(footerGrid);
            panel.Children.Add(bottomButtonRow);

            RebuildBrowserTree(preferredDirectory: "");

            var dialog = new ContentDialog
            {
                Title = L("wildcards.title"),
                Content = panel,
                CloseButtonText = L("button.close"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = ((FrameworkElement)this.Content).RequestedTheme,
            };
            dialog.Resources["ContentDialogMaxWidth"] = 900.0;

            await dialog.ShowAsync();
        }
        finally
        {
            _isWildcardDialogOpen = false;
        }
    }
}
