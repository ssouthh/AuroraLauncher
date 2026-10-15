using System.IO;
using System.Text.RegularExpressions;
using AuroraLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace AuroraLauncher.Pages;

public sealed partial class ModsPage : Page
{
    private CancellationTokenSource? _searchCts;
    private int _currentOffset = 0;
    private const int PageSize = 20;
    private bool _isLoadingMore = false;
    private bool _hasMore = true;
    private string _lastQuery = "";
    private int _selectedTabIndex = 0; // 0 = Catalog, 1 = Installed, 2 = Booster
    private List<string> _cachedInstalledMods = new();

    public ModsPage()
    {
        InitializeComponent();
        Loaded += ModsPage_Loaded;
        Unloaded += ModsPage_Unloaded;
    }

    private void ModsPage_Loaded(object sender, RoutedEventArgs e)
    {
        I18n.LanguageChanged += OnLanguageChanged;
        ApplyLocalization();

        ModsScrollViewer.ViewChanged += ModsScrollViewer_ViewChanged;
        RefreshInstancesSelector();
        RefreshInstalledMods();
        UpdateBoosterTab();
        _ = LoadModsListAsync(append: false);
    }

    private void ModsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        I18n.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            ApplyLocalization();
            RefreshInstalledMods();
            UpdateBoosterTab();
        });
    }

    public void ApplyLocalization()
    {
        ModsPageTitle.Text = I18n.Get("Mods.Title");
        ModsPageSubtitle.Text = I18n.Get("Mods.Subtitle");
        InstanceSelectorLabel.Text = I18n.Get("Mods.InstanceLabel");

        TabCatalogText.Text = I18n.Get("Mods.TabCatalog");
        TabInstalledText.Text = I18n.Get("Mods.TabInstalled", _cachedInstalledMods.Count);
        TabBoosterText.Text = I18n.Get("Mods.TabBooster");

        ToolTipService.SetToolTip(TabCatalogBtn, I18n.Get("Mods.TabCatalog"));
        ToolTipService.SetToolTip(TabInstalledBtn, I18n.Get("Mods.TabInstalled", _cachedInstalledMods.Count));
        ToolTipService.SetToolTip(TabBoosterBtn, I18n.Get("Mods.TabBooster"));

        ModSearchBox.PlaceholderText = I18n.Get("Mods.SearchPlaceholder");
        SearchModsButtonText.Text = I18n.Get("Mods.SearchBtn");
        OpenModsFolderBtnText.Text = I18n.Get("Home.Mods");
        LoadingMoreText.Text = I18n.Get("Mods.LoadingMore");
        EmptySearchStatusText.Text = I18n.Get("Mods.SearchPlaceholder");

        InstalledSearchBox.PlaceholderText = I18n.Get("Common.Search");
        RefreshInstalledBtnText.Text = I18n.Get("Common.Refresh");
        OpenInstalledFolderBtnText.Text = I18n.Get("Common.OpenFolder");
        DeleteAllModsBtnText.Text = I18n.Get("Common.Clear");

        InstalledHeaderText.Text = I18n.Get("Mods.TabInstalled", _cachedInstalledMods.Count);
        EmptyInstalledTitle.Text = I18n.Get("Mods.TabInstalled", 0);
        EmptyInstalledDesc.Text = I18n.Get("Mods.NoModsFound");
        EmptyInstalledCatalogBtn.Content = I18n.Get("Mods.TabCatalog");

        BoosterHeaderTitle.Text = I18n.Get("Mods.BoosterTitle");
        BoosterPackDesc.Text = I18n.Get("Mods.BoosterDesc");
        BoosterInstalledNoticeText.Text = I18n.Get("Mods.BoosterInstalledInfo");
        InstallFpsPackBtnText.Text = I18n.Get("Mods.BoosterApply");
        UninstallFpsPackBtnText.Text = I18n.Get("Mods.BoosterRemove");
    }

    private void RefreshInstancesSelector()
    {
        InstanceSelectorComboBox.Items.Clear();
        var instances = LauncherState.InstancesList;
        foreach (var inst in instances)
        {
            InstanceSelectorComboBox.Items.Add(inst.Name);
        }

        if (LauncherState.SelectedInstance != null)
        {
            var idx = instances.FindIndex(i => i.Name == LauncherState.SelectedInstance.Name);
            if (idx >= 0) InstanceSelectorComboBox.SelectedIndex = idx;
        }
        else if (instances.Count > 0)
        {
            InstanceSelectorComboBox.SelectedIndex = 0;
            LauncherState.SelectInstance(instances[0]);
        }
    }

    private void InstanceSelectorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InstanceSelectorComboBox.SelectedItem is string name)
        {
            var inst = LauncherState.InstancesList.FirstOrDefault(i => i.Name == name);
            if (inst != null)
            {
                LauncherState.SelectInstance(inst);
                CheckLoaderCompatibility(inst);
                RefreshInstalledMods();
                UpdateBoosterTab();
                _ = LoadModsListAsync(append: false);
            }
        }
    }

    private void CheckLoaderCompatibility(Instance inst)
    {
        if (inst.Loader == "vanilla")
        {
            ModSearchBox.IsEnabled = false;
            SearchModsButton.IsEnabled = false;
            ModSourceComboBox.IsEnabled = false;
            SearchResultsPanel.Children.Clear();
            SearchResultsHeaderText.Text = "Моды недоступны для ванильной сборки";
            EmptySearchBorder.Visibility = Visibility.Visible;
            EmptySearchStatusText.Text = "Ванильный Minecraft не поддерживает сторонние моды напрямую. Создайте сборку с Fabric, Forge или NeoForge на вкладке «Сборки».";
            ShowInfo($"Сборка \"{inst.Name}\" использует чистый ванильный Minecraft. Моды и пакеты FPS поддерживаются на Fabric/Forge/NeoForge.", InfoBarSeverity.Warning);
        }
        else
        {
            ModSearchBox.IsEnabled = true;
            SearchModsButton.IsEnabled = true;
            ModSourceComboBox.IsEnabled = true;
            ModsInfoBar.IsOpen = false;
        }
    }

    // ==========================================
    // ТАБЫ: КАТАЛОГ / УСТАНОВЛЕННЫЕ / 1-КЛИК FPS
    // ==========================================

    private void TabCatalogBtn_Click(object sender, RoutedEventArgs e)
    {
        SwitchTab(0);
    }

    private void TabInstalledBtn_Click(object sender, RoutedEventArgs e)
    {
        SwitchTab(1);
    }

    private void TabBoosterBtn_Click(object sender, RoutedEventArgs e)
    {
        SwitchTab(2);
    }

    private void SwitchTab(int tabIndex)
    {
        _selectedTabIndex = tabIndex;
        var accentStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        var defaultStyle = (Style)Application.Current.Resources["DefaultButtonStyle"];

        TabCatalogBtn.Style = tabIndex == 0 ? accentStyle : defaultStyle;
        TabInstalledBtn.Style = tabIndex == 1 ? accentStyle : defaultStyle;
        TabBoosterBtn.Style = tabIndex == 2 ? accentStyle : defaultStyle;

        CatalogViewPanel.Visibility = tabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        InstalledViewPanel.Visibility = tabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        BoosterViewPanel.Visibility = tabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

        if (tabIndex == 1)
        {
            RefreshInstalledMods();
        }
        else if (tabIndex == 2)
        {
            UpdateBoosterTab();
        }
    }

    // ==========================================
    // БЕСКОНЕЧНАЯ ЛЕНТА (INFINITE SCROLL)
    // ==========================================

    private void ModsScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_selectedTabIndex != 0) return;
        if (_isLoadingMore || !_hasMore) return;

        // Если пользователь долистал до нижней границы (за 350px до конца)
        if (ModsScrollViewer.VerticalOffset >= ModsScrollViewer.ScrollableHeight - 350 && ModsScrollViewer.ScrollableHeight > 0)
        {
            _ = LoadModsListAsync(append: true);
        }
    }

    private void ModSearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            _ = LoadModsListAsync(append: false);
        }
    }

    private void SearchModsButton_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadModsListAsync(append: false);
    }

    private void ModSourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            _ = LoadModsListAsync(append: false);
        }
    }

    private async Task LoadModsListAsync(bool append = false)
    {
        var inst = LauncherState.SelectedInstance;
        if (inst == null)
        {
            ShowInfo("Сначала выберите сборку Minecraft.", InfoBarSeverity.Warning);
            return;
        }

        if (inst.Loader == "vanilla")
        {
            CheckLoaderCompatibility(inst);
            return;
        }

        var query = ModSearchBox.Text?.Trim() ?? "";
        var isCurse = ModSourceComboBox.SelectedIndex == 1;

        if (isCurse && string.IsNullOrWhiteSpace(Ctx.Settings.CurseApiKey))
        {
            ShowInfo("Для поиска в CurseForge необходимо указать CurseForge API Key в Настройках.", InfoBarSeverity.Warning);
            return;
        }

        if (!append)
        {
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            _currentOffset = 0;
            _hasMore = true;
            _lastQuery = query;

            SearchProgressRing.Visibility = Visibility.Visible;
            SearchProgressRing.IsActive = true;
            SearchModsButton.IsEnabled = false;
            SearchResultsPanel.Children.Clear();
            EmptySearchBorder.Visibility = Visibility.Collapsed;
        }
        else
        {
            _isLoadingMore = true;
            LoadingMorePanel.Visibility = Visibility.Visible;
        }

        try
        {
            var hits = isCurse
                ? await Mods.SearchCurseForgeAsync(Ctx.Settings.CurseApiKey, query, inst.McVersion, inst.Loader, _searchCts?.Token ?? CancellationToken.None, _currentOffset, PageSize)
                : await Mods.SearchModrinthAsync(query, inst.McVersion, inst.Loader, _searchCts?.Token ?? CancellationToken.None, _currentOffset, PageSize);

            if (!append)
            {
                if (string.IsNullOrWhiteSpace(query))
                    SearchResultsHeaderText.Text = $"🔥 Популярные моды для {inst.Name}";
                else
                    SearchResultsHeaderText.Text = $"Результаты поиска по запросу \"{query}\"";

                if (hits.Count == 0)
                {
                    EmptySearchBorder.Visibility = Visibility.Visible;
                    EmptySearchStatusText.Text = string.IsNullOrWhiteSpace(query)
                        ? $"Нет доступных модов для Minecraft {inst.McVersion} ({inst.Loader})."
                        : $"По запросу \"{query}\" ничего не найдено для Minecraft {inst.McVersion} ({inst.Loader}).";
                    return;
                }
            }

            var installedFiles = Mods.Installed(inst);
            _cachedInstalledMods = installedFiles;

            foreach (var hit in hits)
            {
                var card = CreateModHitCard(hit, inst, installedFiles);
                SearchResultsPanel.Children.Add(card);
            }

            _currentOffset += hits.Count;
            if (hits.Count < PageSize)
            {
                _hasMore = false;
            }
        }
        catch (OperationCanceledException)
        {
            // Отменено
        }
        catch (Exception ex)
        {
            if (!append)
            {
                EmptySearchBorder.Visibility = Visibility.Visible;
                EmptySearchStatusText.Text = "Ошибка при загрузке модов: " + ex.Message;
            }
            ShowInfo("Ошибка: " + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _isLoadingMore = false;
            LoadingMorePanel.Visibility = Visibility.Collapsed;
            SearchProgressRing.IsActive = false;
            SearchProgressRing.Visibility = Visibility.Collapsed;
            SearchModsButton.IsEnabled = true;
        }
    }

    private static bool IsModInstalled(ModHit hit, Instance inst, List<string> installedFiles)
    {
        if (installedFiles == null || installedFiles.Count == 0) return false;

        // 1. Проверка через mods_meta.json
        var meta = Mods.GetInstalledModsMeta(inst);
        if (meta.Count > 0)
        {
            if (meta.TryGetValue(hit.Id.ToLowerInvariant(), out var f1) && installedFiles.Any(f => f.Equals(f1, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (!string.IsNullOrEmpty(hit.Slug) && meta.TryGetValue(hit.Slug.ToLowerInvariant(), out var f2) && installedFiles.Any(f => f.Equals(f2, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (meta.TryGetValue(hit.Title.ToLowerInvariant(), out var f3) && installedFiles.Any(f => f.Equals(f3, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        // 2. Проверка по slug и ID
        var idSlug = hit.Id.Contains(':') ? hit.Id.Substring(hit.Id.IndexOf(':') + 1) : hit.Id;
        var cleanSlug = Regex.Replace(idSlug, @"[^a-zA-Z0-9]", "").ToLowerInvariant();
        var rawSlug = (hit.Slug ?? "").ToLowerInvariant();

        // 3. Первое слово названия (например, "Iris Shaders" -> "iris")
        var firstWord = hit.Title.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
        var cleanTitle = Regex.Replace(hit.Title, @"[^a-zA-Z0-9]", "").ToLowerInvariant();

        foreach (var file in installedFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            var cleanFile = Regex.Replace(fileName, @"[^a-zA-Z0-9]", "");

            if (!string.IsNullOrEmpty(rawSlug) && (cleanFile.StartsWith(rawSlug) || fileName.StartsWith(rawSlug) || cleanFile.Contains(rawSlug)))
                return true;

            if (!string.IsNullOrEmpty(cleanSlug) && (cleanFile.StartsWith(cleanSlug) || fileName.StartsWith(idSlug.ToLowerInvariant())))
                return true;

            if (firstWord.Length >= 3 && (fileName.StartsWith(firstWord) || cleanFile.StartsWith(firstWord)))
                return true;

            if (!string.IsNullOrEmpty(cleanTitle) && (cleanFile.StartsWith(cleanTitle) || fileName.StartsWith(hit.Title.ToLowerInvariant())))
                return true;

            if (cleanTitle.Length >= 4 && cleanFile.Contains(cleanTitle))
                return true;
        }

        return false;
    }

    private UIElement CreateModHitCard(ModHit hit, Instance inst, List<string> installedFiles)
    {
        var isInstalled = IsModInstalled(hit, inst, installedFiles);

        var card = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources[isInstalled ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(isInstalled ? 1.5 : 1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14, 16, 14)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Иконка мода
        var iconBorder = new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            Margin = new Thickness(0, 0, 16, 0)
        };

        if (!string.IsNullOrEmpty(hit.IconUrl))
        {
            var img = new Image
            {
                Source = new BitmapImage(new Uri(hit.IconUrl)),
                Stretch = Stretch.UniformToFill
            };
            iconBorder.Child = img;
        }
        else
        {
            iconBorder.Child = new FontIcon { Glyph = "\uE896", FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        Grid.SetColumn(iconBorder, 0);
        grid.Children.Add(iconBorder);

        // Инфо о моде
        var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 3 };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock { Text = hit.Title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], FontSize = 15 });

        if (isInstalled)
        {
            var installedBadge = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 16, 124, 65)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 124, 65)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 2, 8, 2),
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center
            };
            var badgeStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            badgeStack.Children.Add(new FontIcon { Glyph = "\uE73E", FontSize = 12, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 124, 65)) });
            badgeStack.Children.Add(new TextBlock
            {
                Text = "Установлен",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 124, 65)),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            installedBadge.Child = badgeStack;
            titleRow.Children.Add(installedBadge);
        }

        var sourceBadge = new Border
        {
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            Padding = new Thickness(6, 1, 6, 1),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        };
        sourceBadge.Child = new TextBlock
        {
            Text = hit.Source,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        titleRow.Children.Add(sourceBadge);

        if (!string.IsNullOrEmpty(inst.Loader) && inst.Loader != "vanilla")
        {
            var loaderBadge = new Border
            {
                Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                Padding = new Thickness(6, 1, 6, 1),
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center
            };
            loaderBadge.Child = new TextBlock
            {
                Text = char.ToUpperInvariant(inst.Loader[0]) + inst.Loader.Substring(1),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"]
            };
            titleRow.Children.Add(loaderBadge);
        }

        infoPanel.Children.Add(titleRow);

        var authorDownloads = new TextBlock
        {
            Text = $"👤 {hit.Author}  •  📥 {hit.Downloads} скачиваний",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        infoPanel.Children.Add(authorDownloads);

        if (!string.IsNullOrWhiteSpace(hit.Description))
        {
            infoPanel.Children.Add(new TextBlock
            {
                Text = hit.Description,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2
            });
        }

        Grid.SetColumn(infoPanel, 1);
        grid.Children.Add(infoPanel);

        // Кнопка установки
        var actionPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var installBtn = new Button
        {
            Style = (Style)Application.Current.Resources[isInstalled ? "DefaultButtonStyle" : "AccentButtonStyle"],
            Height = 38,
            Padding = new Thickness(18, 0, 18, 0),
            IsEnabled = !isInstalled
        };
        var btnContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var btnIcon = new FontIcon { Glyph = isInstalled ? "\uE73E" : "\uE896", FontSize = 14 };
        var btnText = new TextBlock { Text = isInstalled ? "Установлен" : "Установить", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var btnRing = new ProgressRing { Width = 14, Height = 14, IsActive = false, Visibility = Visibility.Collapsed };

        btnContent.Children.Add(btnIcon);
        btnContent.Children.Add(btnRing);
        btnContent.Children.Add(btnText);
        installBtn.Content = btnContent;

        installBtn.Click += async (_, _) =>
        {
            installBtn.IsEnabled = false;
            btnIcon.Visibility = Visibility.Collapsed;
            btnRing.Visibility = Visibility.Visible;
            btnRing.IsActive = true;
            btnText.Text = "Скачивание...";

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var file = await Mods.InstallAsync(hit.Id, inst, cts.Token);
                Mods.RecordInstalledMod(inst, hit.Id, hit.Slug, hit.Title, file);

                btnText.Text = "Установлен";
                btnRing.IsActive = false;
                btnRing.Visibility = Visibility.Collapsed;
                btnIcon.Glyph = "\uE73E";
                btnIcon.Visibility = Visibility.Visible;
                installBtn.IsEnabled = false;
                installBtn.Style = (Style)Application.Current.Resources["DefaultButtonStyle"];
                card.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
                card.BorderThickness = new Thickness(1.5);

                if (!isInstalled)
                {
                    var installedBadge = new Border
                    {
                        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 16, 124, 65)),
                        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 124, 65)),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(8, 2, 8, 2),
                        CornerRadius = new CornerRadius(4),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var badgeStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                    badgeStack.Children.Add(new FontIcon { Glyph = "\uE73E", FontSize = 12, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 124, 65)) });
                    badgeStack.Children.Add(new TextBlock
                    {
                        Text = "Установлен",
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 124, 65)),
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    });
                    installedBadge.Child = badgeStack;
                    titleRow.Children.Insert(1, installedBadge);
                    isInstalled = true;
                }

                ShowInfo($"Мод \"{hit.Title}\" ({file}) успешно добавлен в сборку \"{inst.Name}\"!", InfoBarSeverity.Success);
                RefreshInstalledMods();
                UpdateBoosterTab();
            }
            catch (Exception ex)
            {
                installBtn.IsEnabled = true;
                btnRing.IsActive = false;
                btnRing.Visibility = Visibility.Collapsed;
                btnIcon.Visibility = Visibility.Visible;
                btnText.Text = "Установить";
                ShowInfo($"Не удалось установить {hit.Title}: {ex.Message}", InfoBarSeverity.Error);
            }
        };

        actionPanel.Children.Add(installBtn);
        Grid.SetColumn(actionPanel, 2);
        grid.Children.Add(actionPanel);

        card.Child = grid;
        return card;
    }

    // ==========================================
    // УПРАВЛЕНИЕ УСТАНОВЛЕННЫМИ МОДАМИ
    // ==========================================

    private void RefreshInstalledMods()
    {
        var inst = LauncherState.SelectedInstance;
        if (inst == null)
        {
            EmptyInstalledBorder.Visibility = Visibility.Visible;
            InstalledHeaderText.Text = "Установленные моды";
            TabInstalledText.Text = "📦 Установленные моды (0)";
            return;
        }

        var files = Mods.Installed(inst);
        _cachedInstalledMods = files;
        TabInstalledText.Text = $"📦 Установленные моды ({files.Count})";

        RenderInstalledList(InstalledSearchBox?.Text ?? "");
    }

    private void RenderInstalledList(string filter)
    {
        InstalledModsPanel.Children.Clear();
        var inst = LauncherState.SelectedInstance;
        if (inst == null) return;

        var files = _cachedInstalledMods;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            files = files.Where(f => f.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        InstalledHeaderText.Text = $"Установленные моды ({files.Count})";

        if (files.Count == 0)
        {
            EmptyInstalledBorder.Visibility = Visibility.Visible;
            return;
        }

        EmptyInstalledBorder.Visibility = Visibility.Collapsed;
        foreach (var file in files)
        {
            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 10, 16, 10)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new FontIcon { Glyph = "\uE896", FontSize = 18, Margin = new Thickness(0, 0, 14, 0), Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] };
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            textPanel.Children.Add(new TextBlock
            {
                Text = file,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            textPanel.Children.Add(new TextBlock
            {
                Text = "Активный файл мода (.jar)",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });

            Grid.SetColumn(textPanel, 1);
            grid.Children.Add(textPanel);

            var deleteBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE74D", FontSize = 14, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 230, 60, 60)) },
                Padding = new Thickness(10, 6, 10, 6)
            };
            ToolTipService.SetToolTip(deleteBtn, "Удалить мод из сборки");

            deleteBtn.Click += async (_, _) =>
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Удалить мод?",
                    Content = $"Удалить файл \"{file}\" из сборки?",
                    PrimaryButtonText = "Удалить",
                    CloseButtonText = "Отмена"
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    Mods.Delete(inst, file);
                    RefreshInstalledMods();
                    ShowInfo($"Файл {file} удален.", InfoBarSeverity.Informational);
                    _ = LoadModsListAsync(append: false);
                }
            };

            Grid.SetColumn(deleteBtn, 2);
            grid.Children.Add(deleteBtn);

            card.Child = grid;
            InstalledModsPanel.Children.Add(card);
        }
    }

    private void InstalledSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RenderInstalledList(InstalledSearchBox.Text?.Trim() ?? "");
    }

    private void RefreshInstalledBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshInstalledMods();
    }

    private async void DeleteAllModsBtn_Click(object sender, RoutedEventArgs e)
    {
        var inst = LauncherState.SelectedInstance;
        if (inst == null || _cachedInstalledMods.Count == 0) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Удалить ВСЕ моды?",
            Content = $"Вы действительно хотите удалить все {_cachedInstalledMods.Count} модов из сборки \"{inst.Name}\"?",
            PrimaryButtonText = "Удалить все",
            CloseButtonText = "Отмена"
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            foreach (var f in _cachedInstalledMods.ToList())
            {
                Mods.Delete(inst, f);
            }
            RefreshInstalledMods();
            ShowInfo("Все моды удалены из сборки.", InfoBarSeverity.Informational);
            _ = LoadModsListAsync(append: false);
        }
    }

    // ==========================================
    // 1-КЛИК УСКОРИТЕЛЬ FPS
    // ==========================================

    private void UpdateBoosterTab()
    {
        var inst = LauncherState.SelectedInstance;
        if (inst == null) return;

        BoosterTargetText.Text = $"Целевая сборка: {inst.Name} (Minecraft {inst.McVersion}, {inst.Loader})";

        var pack = FpsBooster.GetRecommendedPack(inst);
        BoosterPackTitle.Text = pack.Name;
        BoosterPackDesc.Text = pack.Description;

        var isInstalled = FpsBooster.IsPackInstalled(inst);
        BoosterInstalledNotice.Visibility = isInstalled ? Visibility.Visible : Visibility.Collapsed;
        InstallFpsPackBtn.Visibility = isInstalled ? Visibility.Collapsed : Visibility.Visible;
        UninstallFpsPackBtn.Visibility = isInstalled ? Visibility.Visible : Visibility.Collapsed;

        InstallFpsPackBtn.IsEnabled = inst.Loader != "vanilla";
    }

    private void UninstallFpsPackBtn_Click(object sender, RoutedEventArgs e)
    {
        var inst = LauncherState.SelectedInstance;
        if (inst == null) return;

        var removed = FpsBooster.UninstallFpsPack(inst);
        ShowInfo($"Пакет оптимизации FPS удалён ({removed} файлов удалено).", InfoBarSeverity.Informational);
        RefreshInstalledMods();
        UpdateBoosterTab();
        _ = LoadModsListAsync(append: false);
    }

    private async void InstallFpsPackBtn_Click(object sender, RoutedEventArgs e)
    {
        var inst = LauncherState.SelectedInstance;
        if (inst == null) return;

        InstallFpsPackBtn.IsEnabled = false;
        BoosterProgressPanel.Visibility = Visibility.Visible;

        var progress = new Progress<string>(status =>
        {
            BoosterStatusText.Text = status;
        });

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var installed = await FpsBooster.InstallFpsPackAsync(inst, progress, cts.Token);
            ShowInfo($"⚡ Успешно установлено {installed.Count} компонентов оптимизации FPS!", InfoBarSeverity.Success);
            RefreshInstalledMods();
            UpdateBoosterTab();
        }
        catch (Exception ex)
        {
            ShowInfo($"Ошибка установки пакета FPS: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            InstallFpsPackBtn.IsEnabled = true;
            BoosterProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OpenModsFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var inst = LauncherState.SelectedInstance;
        if (inst != null)
        {
            Instances.OpenFolder(inst);
        }
        else
        {
            ShowInfo("Сначала выберите сборку.", InfoBarSeverity.Warning);
        }
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        ModsInfoBar.Message = message;
        ModsInfoBar.Severity = severity;
        ModsInfoBar.IsOpen = true;
    }
}
