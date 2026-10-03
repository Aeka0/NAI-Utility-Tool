using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAITool.Services;

namespace NAITool;

public sealed partial class MainWindow
{
    private UIElement BuildApiSettingsPage()
    {
        var rows = new StackPanel { Spacing = 10 };
        var editors = new List<(Grid Row, PasswordBox Token, TextBlock Status, TextBlock Label, Button Remove, FontIcon RemoveIcon)>();
        var addButton = new Button { Content = L("settings.api.add") };
        var testButton = new Button { Content = L("settings.api.test_all") };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
        bool testing = false;
        CancellationTokenSource? testCts = null;

        void SetRowsEnabled(bool enabled)
        {
            foreach (var editor in editors)
                foreach (var control in editor.Row.Children.OfType<Control>())
                    control.IsEnabled = enabled;
        }

        void RefreshRowLabels()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < editors.Count; i++)
            {
                var editor = editors[i];
                editor.Label.Text = Lf("settings.api.account_number", i + 1);
                bool clearOnly = editors.Count == 1;
                editor.RemoveIcon.Glyph = clearOnly ? "\uE711" : "\uE74D";
                string actionLabel = L(clearOnly ? "settings.api.clear" : "settings.api.remove");
                ToolTipService.SetToolTip(editor.Remove, actionLabel);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(editor.Remove, actionLabel);
                string token = editor.Token.Password.Trim();
                editor.Status.Text = token.Length > 0 && !seen.Add(token) ? L("settings.api.duplicate") : "";
            }
        }

        void ApplyTokens(bool save)
        {
            _settings.SetApiTokens(editors.Select(e => e.Token.Password));
            RefreshRowLabels();
            _anlasInitialFetchDone = false;
            summary.Text = "";
            UpdateAccountUi();
            if (save) _settings.Save();
        }

        void AddRow(string token)
        {
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            var box = new PasswordBox
            {
                Password = token,
                PlaceholderText = L("settings.hub.network.api_token_placeholder"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            var removeIcon = new FontIcon { FontFamily = SymbolFontFamily, FontSize = 14 };
            var remove = new Button
            {
                Content = removeIcon,
                Width = 32,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            var status = new TextBlock { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
            var row = new Grid { ColumnSpacing = 10, RowSpacing = 4 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(box, 1);
            Grid.SetColumn(remove, 2);
            Grid.SetColumn(status, 1);
            Grid.SetColumnSpan(status, 2);
            Grid.SetRow(status, 1);
            row.Children.Add(label);
            row.Children.Add(box);
            row.Children.Add(remove);
            row.Children.Add(status);
            editors.Add((row, box, status, label, remove, removeIcon));
            rows.Children.Add(row);
            box.PasswordChanged += (_, _) => ApplyTokens(save: false);
            box.LostFocus += (_, _) => _settings.Save();
            remove.Click += (_, _) =>
            {
                if (editors.Count == 1) box.Password = "";
                else
                {
                    editors.RemoveAll(e => e.Row == row);
                    rows.Children.Remove(row);
                }
                ApplyTokens(save: true);
            };
        }

        foreach (var account in _settings.Accounts) AddRow(account.Token);
        if (editors.Count == 0) AddRow("");
        RefreshRowLabels();
        addButton.Click += (_, _) =>
        {
            AddRow("");
            ApplyTokens(save: true);
            editors[^1].Token.Focus(FocusState.Programmatic);
        };

        var baseUrlBox = new TextBox
        {
            Text = _settings.Settings.ApiBaseUrl,
            PlaceholderText = L("settings.hub.network.base_url_placeholder"),
            Width = SettingsHubControlColumnWidth,
        };
        void SaveEndpoint()
        {
            string url = AppSettings.NormalizeApiBaseUrl(baseUrlBox.Text);
            if (url != _settings.Settings.ApiBaseUrl)
            {
                _settings.Settings.ApiBaseUrl = url;
                foreach (var account in _settings.Accounts)
                {
                    account.Info = null;
                    account.IsTokenValid = null;
                    account.RefreshedAt = DateTimeOffset.MinValue;
                }
                ApplyTokens(save: false);
            }
            _settings.Save();
        }
        baseUrlBox.LostFocus += (_, _) => SaveEndpoint();

        testButton.Click += async (_, _) =>
        {
            if (testing) return;
            SaveEndpoint();
            ApplyTokens(save: true);
            if (!AppSettings.IsValidApiBaseUrl(baseUrlBox.Text))
            {
                summary.Text = L("settings.network.invalid_api_or_network");
                return;
            }
            testing = true;
            SetRowsEnabled(false);
            addButton.IsEnabled = testButton.IsEnabled = baseUrlBox.IsEnabled = false;
            testCts = new CancellationTokenSource();
            var ct = testCts.Token;
            try
            {
                var accounts = ApiAccountRouter.UniqueAccounts(_settings.Accounts);
                summary.Text = L("settings.network.testing");
                var results = new System.Collections.Concurrent.ConcurrentDictionary<string, (bool Success, string Message)>();
                await Parallel.ForEachAsync(accounts, new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4, CancellationToken = ct,
                }, async (account, token) =>
                {
                    var result = await _naiService.TestConnectionAsync(account.Token, token);
                    results[account.Token] = result;
                    if (result.Success) await _naiService.RefreshAccountAsync(account, token);
                });
                ct.ThrowIfCancellationRequested();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var editor in editors)
                {
                    string token = editor.Token.Password.Trim();
                    editor.Status.Text = token.Length == 0 ? L("oobe.api.test.empty")
                        : !seen.Add(token) ? L("settings.api.duplicate")
                        : results.TryGetValue(token, out var result) ? result.Message : "";
                }
                summary.Text = Lf("settings.api.test_summary", results.Count(r => r.Value.Success), accounts.Length);
                _settings.Save();
                UpdateAccountUi();
            }
            catch (OperationCanceledException) { }
            finally
            {
                testing = false;
                SetRowsEnabled(true);
                addButton.IsEnabled = testButton.IsEnabled = baseUrlBox.IsEnabled = true;
                testCts.Dispose();
                testCts = null;
            }
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(addButton);
        actions.Children.Add(testButton);
        var tokenPanel = new StackPanel { Spacing = 12 };
        tokenPanel.Children.Add(rows);
        tokenPanel.Children.Add(actions);
        tokenPanel.Children.Add(summary);
        var modeBox = new ComboBox { Width = SettingsHubControlColumnWidth };
        foreach (string key in new[] { "round_robin", "most_quota", "least_quota", "random" })
            modeBox.Items.Add(L("settings.api.mode." + key));
        modeBox.SelectedIndex = (int)_settings.Settings.ApiCallMode;
        modeBox.SelectionChanged += (_, _) =>
        {
            _settings.Settings.ApiCallMode = (ApiCallMode)Math.Max(0, modeBox.SelectedIndex);
            _settings.Save();
            UpdateGenerateButtonWarning();
        };
        var page = CreateSettingsHubPage(
            CreateSettingsHubLayer("\uE71B", L("settings.hub.network.base_url"),
                L("settings.hub.network.base_url_hint"), baseUrlBox),
            CreateSettingsHubLayer("\uE8D7", L("settings.hub.network.api_token"),
                L("settings.api.tokens_hint"), tokenPanel, expanded: true),
            CreateSettingsHubLayer("\uE8AB", L("settings.api.mode"), L("settings.api.mode_hint"), modeBox));
        ((FrameworkElement)page).Unloaded += (_, _) =>
        {
            testCts?.Cancel();
            SaveEndpoint();
        };
        return page;
    }
}
