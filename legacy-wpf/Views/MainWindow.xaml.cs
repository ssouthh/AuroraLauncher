using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AuroraLauncher.Core;

namespace AuroraLauncher.Views;

public partial class MainWindow : Window
{
    private List<Instance> _instances = new();
    private List<JsonVersionEntry> _allVersions = new();
    private Instance? SelectedInstance => InstanceList.SelectedItem as Instance;

    public MainWindow()
    {
        InitializeComponent();
        RamSlider.ValueChanged += (_, _) =>
            RamText.Text = ((int)RamSlider.Value).ToString("N0") + " МБ";
        Loaded += async (_, _) => await OnLoadedAsync();
    }

    private async Task OnLoadedAsync()
    {
        LoadSettingsToUi();
        RefreshInstances();
        RefreshAccountUi();
        LoaderCombo.ItemsSource = Loaders.Supported.Select(l => l.Title).ToList();
        LoaderCombo.SelectedIndex = 0;
        await LoadVersionListAsync();
        if (Ctx.Account == null && File.Exists(Paths.AccountsFile) == false)
        {
            HomeStatus.Text = "Выполните вход через Microsoft в левом нижнем углу.";
        }
    }

    // ================= Навигация =================

    private void SwitchPage(string page)
    {
        // RadioButtons порождают Checked ещё в InitializeComponent — до создания страниц
        if (PageHome == null || PageVersions == null || PageMods == null || PageSettings == null) return;
        PageHome.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        PageVersions.Visibility = page == "versions" ? Visibility.Visible : Visibility.Collapsed;
        PageMods.Visibility = page == "mods" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "mods") RefreshModsTarget();
        if (page == "home") RefreshInstances();
    }

    private void NavHome_Checked(object sender, RoutedEventArgs e) => SwitchPage("home");
    private void NavVersions_Checked(object sender, RoutedEventArgs e) => SwitchPage("versions");
    private void NavMods_Checked(object sender, RoutedEventArgs e) => SwitchPage("mods");
    private void NavSettings_Checked(object sender, RoutedEventArgs e) => SwitchPage("settings");

    // ================= Главная =================

    private void RefreshInstances()
    {
        _instances = Instances.LoadAll();
        var selName = (InstanceList.SelectedItem as Instance)?.Name;
        InstanceList.ItemsSource = _instances;
        if (selName != null)
            InstanceList.SelectedItem = _instances.FirstOrDefault(i => i.Name == selName);
        RefreshModsTarget();
    }

    private void InstanceList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshModsTarget();

    private async void PlayBtn_Click(object sender, RoutedEventArgs e)
    {
        var inst = SelectedInstance;
        if (inst == null)
        {
            MessageBox.Show(this, "Выберите сборку в списке (или создайте на вкладке «Версии»).",
                "Aurora", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (Ctx.Account == null)
        {
            AccountBtn_Click(sender, e);
            if (Ctx.Account == null) return;
        }

        await RunWithProgressAsync("Запуск " + inst.Name, async (prog, ct) =>
        {
            var proc = await GameLauncher.LaunchAsync(inst, prog, ct);
            await Task.Delay(1500, CancellationToken.None); // дать игре время стартовать
        });
    }

    private async void DeleteInstance_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Instance inst) return;
        var r = MessageBox.Show(this, $"Удалить сборку «{inst.Name}» вместе с мирами и модами?",
            "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        try
        {
            if (Directory.Exists(inst.Dir))
            {
                await Task.Run(() =>
                {
                    foreach (var f in Directory.EnumerateFiles(inst.Dir, "*", SearchOption.AllDirectories)
                                 .Select(p => new FileInfo(p) { IsReadOnly = false }))
                        f.Delete();
                    Directory.Delete(inst.Dir, true);
                });
            }
            _instances.RemoveAll(i => i.Name == inst.Name);
            Instances.SaveAll(_instances);
            RefreshInstances();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось удалить: " + ex.Message);
        }
    }

    // ================= Версии =================

    private sealed record JsonVersionEntry(string Id, string Type, string Url)
    {
        public override string ToString() => Id;
    }

    private async Task LoadVersionListAsync()
    {
        try
        {
            VersionsStatus.Text = "Загрузка списка версий…";
            var manifest = await Mojang.GetManifestAsync(ct: CancellationToken.None);
            _allVersions = manifest.Arr("versions")
                .Where(v => v.Str("type") is "release" or "snapshot" or "old_alpha" or "old_beta")
                .Select(v => new JsonVersionEntry(v.Str("id")!, v.Str("type")!, v.Str("url")!))
                .ToList();
            // По умолчанию показываем релизы
            FillMcVersions("release");
            VersionsStatus.Text = $"Версий в манифесте: {_allVersions.Count}";
        }
        catch (Exception ex)
        {
            VersionsStatus.Text = "Ошибка загрузки манифеста: " + ex.Message;
        }
    }

    private void FillMcVersions(string type)
    {
        var list = _allVersions.Where(v => v.Type == type).ToList();
        McVersionCombo.ItemsSource = list;
        if (list.Count > 0) McVersionCombo.SelectedIndex = 0;
    }

    private void LoaderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var loader = CurrentLoaderId();
        // Для отображения версий игры: у загрузчиков логичны релизы
        if (_allVersions.Count > 0) FillMcVersions("release");
        _ = LoadLoaderVersionsAsync();
    }

    private async void McVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => await LoadLoaderVersionsAsync();

    private string CurrentLoaderId()
    {
        var idx = LoaderCombo.SelectedIndex;
        return idx >= 0 && idx < Loaders.Supported.Count ? Loaders.Supported[idx].Id : "vanilla";
    }

    private async Task LoadLoaderVersionsAsync()
    {
        var loader = CurrentLoaderId();
        var mc = (McVersionCombo.SelectedItem as JsonVersionEntry)?.Id;
        LoaderVersionCombo.IsEnabled = loader != "vanilla";
        if (loader == "vanilla" || mc == null) return;

        try
        {
            LoaderVersionCombo.ItemsSource = new[] { "последняя совместимая" };
            LoaderVersionCombo.SelectedIndex = 0;
            var versions = loader switch
            {
                "fabric" => await Loaders.FabricVersionsForAsync(mc, CancellationToken.None),
                "neoforge" => await Loaders.NeoForgeVersionsForAsync(mc, CancellationToken.None),
                "forge" => await Loaders.ForgePromosAsync(mc, CancellationToken.None),
                _ => new List<string>(),
            };
            var items = new List<string> { "последняя совместимая" };
            items.AddRange(versions);
            LoaderVersionCombo.ItemsSource = items;
            if (items.Count <= 1)
                Ctx.Log($"Для {mc}/{loader} список версий пуст — будет взята последняя совместимая автоматически.");
        }
        catch (Exception ex)
        {
            Ctx.Log("Не удалось получить версии загрузчика: " + ex.Message);
        }
    }

    private async void CreateInstance_Click(object sender, RoutedEventArgs e)
    {
        var loader = CurrentLoaderId();
        var mc = (McVersionCombo.SelectedItem as JsonVersionEntry)?.Id;
        if (mc == null)
        {
            MessageBox.Show(this, "Выберите версию Minecraft.");
            return;
        }

        var loaderVersion = (LoaderVersionCombo.SelectedItem as string) ?? "";
        if (loaderVersion.StartsWith("последняя")) loaderVersion = "";

        var baseName = InstanceNameBox.Text.Trim();
        var name = string.IsNullOrEmpty(baseName)
            ? (loader == "vanilla" ? mc : $"{mc} ({loader})")
            : baseName;

        _instances = Instances.LoadAll();
        var uniq = name;
        var n = 2;
        while (_instances.Any(i => i.Name.Equals(uniq, StringComparison.OrdinalIgnoreCase)))
            uniq = $"{name} ({n++})";

        var inst = new Instance
        {
            Name = uniq,
            McVersion = mc,
            Loader = loader,
            LoaderVersion = loaderVersion,
        };

        CreateBtn.IsEnabled = false;
        try
        {
            await RunWithProgressAsync($"Установка {uniq}", async (prog, ct) =>
            {
                await Instances.PrepareAsync(inst, prog, ct);
            });
            _instances.Add(inst);
            Instances.SaveAll(_instances);
            RefreshInstances();
            InstanceList.SelectedItem = _instances.FirstOrDefault(i => i.Name == inst.Name);
            NavHome.IsChecked = true;
            SwitchPage("home");
            HomeStatus.Text = $"Сборка «{inst.Subtitle}» готова. Жмите ▶ ИГРАТЬ.";
        }
        catch (OperationCanceledException)
        {
            HomeStatus.Text = "Установка отменена.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось создать сборку: " + ex.Message, "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Ctx.Log("Ошибка установки: " + ex);
        }
        finally
        {
            CreateBtn.IsEnabled = true;
        }
    }

    // ================= Моды =================

    private void RefreshModsTarget()
    {
        // ComboBox источников модов может вызвать SelectionChanged ещё в InitializeComponent,
        // когда соседние элементы вкладки ещё не созданы
        if (ModsTargetText == null || ModSearchBox == null || InstalledModsList == null || ModResults == null) return;
        var inst = SelectedInstance;
        ModsTargetText.Text = inst == null
            ? "Выберите сборку на главной вкладке — моды установятся в неё."
            : $"Сборка: {inst.Subtitle}. Поиск учитывает версию игры и загрузчик.";
        if (inst != null)
        {
            InstalledModsList.ItemsSource = Mods.Installed(inst);
            var loaderParam = inst.Loader == "vanilla" ? null : inst.Loader;
            _ = Task.Run(async () =>
            {
                try
                {
                    var q = ModSearchBox.Text.Trim();
                    var results = ModSourceCombo.SelectedIndex == 1
                        ? await Mods.SearchCurseForgeAsync(Ctx.Settings.CurseApiKey, string.IsNullOrEmpty(q) ? "jei" : q, inst.McVersion, CancellationToken.None)
                        : await Mods.SearchModrinthAsync(string.IsNullOrEmpty(q) ? "jei" : q, inst.McVersion, loaderParam, CancellationToken.None);
                    Dispatcher.Invoke(() => ModResults.ItemsSource = results);
                }
                catch (Exception ex) { Ctx.Log("Поиск модов: " + ex.Message); }
            });
        }
        else
        {
            ModResults.ItemsSource = null;
            InstalledModsList.ItemsSource = null;
        }
    }

    private void ModSource_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshModsTarget();

    private async void ModSearch_Click(object sender, RoutedEventArgs e) => await DoSearchAsync();

    private async void ModSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await DoSearchAsync();
    }

    private async Task DoSearchAsync()
    {
        var inst = SelectedInstance;
        if (inst == null)
        {
            MessageBox.Show(this, "Сначала выберите сборку на вкладке «Главная».");
            return;
        }
        var q = ModSearchBox.Text.Trim();
        ModSearchBtn.IsEnabled = false;
        try
        {
            List<ModHit> results;
            if (ModSourceCombo.SelectedIndex == 1)
            {
                if (string.IsNullOrWhiteSpace(Ctx.Settings.CurseApiKey))
                {
                    MessageBox.Show(this, "Для поиска на CurseForge укажите API-ключ в настройках (он бесплатный для разработчиков).");
                    return;
                }
                results = await Mods.SearchCurseForgeAsync(Ctx.Settings.CurseApiKey, q, inst.McVersion, CancellationToken.None);
            }
            else
            {
                results = await Mods.SearchModrinthAsync(q, inst.McVersion, inst.Loader == "vanilla" ? null : inst.Loader, CancellationToken.None);
            }
            ModResults.ItemsSource = results;
            if (results.Count == 0) HomeStatus.Text = "Ничего не найдено.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Ошибка поиска: " + ex.Message);
        }
        finally
        {
            ModSearchBtn.IsEnabled = true;
        }
    }

    private async void InstallMod_Click(object sender, RoutedEventArgs e)
    {
        var inst = SelectedInstance;
        if (inst == null) return;
        if ((sender as FrameworkElement)?.DataContext is not ModHit hit) return;
        try
        {
            var fname = await RunWithProgressAsync($"Установка мода {hit.Title}", async (prog, ct) =>
            {
                var res = "";
                await Task.Run(async () => res = await Mods.InstallAsync(hit.Id, inst, ct), ct);
                return res;
            });
            Ctx.Log($"Мод {hit.Title} установлен: {fname}");
            InstalledModsList.ItemsSource = Mods.Installed(inst);
            HomeStatus.Text = $"Мод «{hit.Title}» установлен в сборку «{inst.Name}».";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось установить мод: " + ex.Message, "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteMod_Click(object sender, RoutedEventArgs e)
    {
        var inst = SelectedInstance;
        if (inst == null || (sender as FrameworkElement)?.DataContext is not string fileName) return;
        Mods.Delete(inst, fileName);
        InstalledModsList.ItemsSource = Mods.Installed(inst);
    }

    // ================= Настройки =================

    private void LoadSettingsToUi()
    {
        var s = Ctx.Settings;
        RamSlider.Value = s.RamMb;
        JavaPathBox.Text = s.JavaPath;
        ResCheck.IsChecked = s.CustomResolution;
        WidthBox.Text = s.WindowWidth.ToString();
        HeightBox.Text = s.WindowHeight.ToString();
        JvmArgsBox.Text = s.ExtraJvmArgs;
        CurseKeyBox.Text = s.CurseApiKey;
        ClientIdBox.Text = s.MsClientId;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var s = Ctx.Settings;
        s.RamMb = (int)RamSlider.Value;
        s.JavaPath = JavaPathBox.Text.Trim();
        s.CustomResolution = ResCheck.IsChecked == true;
        if (int.TryParse(WidthBox.Text, out var w)) s.WindowWidth = Math.Clamp(w, 320, 7680);
        if (int.TryParse(HeightBox.Text, out var h)) s.WindowHeight = Math.Clamp(h, 240, 4320);
        s.ExtraJvmArgs = JvmArgsBox.Text.Trim();
        s.CurseApiKey = CurseKeyBox.Text.Trim();
        s.MsClientId = ClientIdBox.Text.Trim();
        Ctx.SaveSettings();
        SettingsStatus.Text = "Сохранено ✓";
        _ = Task.Delay(2500).ContinueWith(_ => Dispatcher.Invoke(() => SettingsStatus.Text = ""));
    }

    private void PickJava_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите java.exe или javaw.exe",
            Filter = "java.exe|java.exe;javaw.exe|Все файлы|*.*",
        };
        if (dlg.ShowDialog(this) == true) JavaPathBox.Text = dlg.FileName;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Proc.OpenInExplorer(Paths.Root);

    // ================= Аккаунт =================

    private void RefreshAccountUi()
    {
        var a = Ctx.Account;
        if (a != null)
        {
            AccountName.Text = a.Username;
            AccountStatus.Text = "Microsoft · лицензия";
            AvatarChar.Text = string.IsNullOrEmpty(a.Username) ? "?" : a.Username[..1].ToUpperInvariant();
            AccountBtn.Content = "Выйти";
        }
        else
        {
            AccountName.Text = "Не выполнен вход";
            AccountStatus.Text = "Microsoft аккаунт";
            AvatarChar.Text = "?";
            AccountBtn.Content = "Войти";
        }
    }

    private async void AccountBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Ctx.Account != null)
        {
            var r = MessageBox.Show(this, $"Выйти из аккаунта {Ctx.Account.Username}?",
                "Выход", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            Accounts.Delete();
            Ctx.Account = null;
            RefreshAccountUi();
            return;
        }
        var win = new LoginWindow { Owner = this };
        win.ShowDialog();
        RefreshAccountUi();
        if (win.Success)
            HomeStatus.Text = $"Вы вошли как {Ctx.Account?.Username}. Выберите сборку и жмите ▶ ИГРАТЬ.";
        await Task.CompletedTask;
    }

    // ================= Вспомогательное =================

    private async Task RunWithProgressAsync(string title, Func<IProgress<ProgressReport>, CancellationToken, Task> work)
    {
        var win = new ProgressWindow(title) { Owner = this };
        try
        {
            await work(win.Progress, win.Token);
        }
        catch (OperationCanceledException)
        {
            Ctx.Log("Операция отменена: " + title);
        }
        catch (Exception ex)
        {
            Ctx.Log("Ошибка: " + title + " — " + ex.Message);
            MessageBox.Show(this, ex.Message, title + " — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            win.Close();
        }
    }

    private async Task<string> RunWithProgressAsync(string title, Func<IProgress<ProgressReport>, CancellationToken, Task<string>> work)
    {
        string result = "";
        var win = new ProgressWindow(title) { Owner = this };
        try
        {
            result = await work(win.Progress, win.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Ctx.Log("Ошибка: " + title + " — " + ex.Message);
            MessageBox.Show(this, ex.Message, title + " — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            win.Close();
        }
        return result;
    }
}
