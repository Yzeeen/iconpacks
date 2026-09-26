using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using DesktopIconChanger.Models;
using DesktopIconChanger.Services;
using FluentIcons.Common;

namespace DesktopIconChanger;

public partial class MainWindow : Window
{
    // ============== 原始字段 ==============
    private ObservableCollection<DesktopIconItem> _items = new();
    private string? _selectedNewIconPath;
    private string? _resolvedIcoPath;
    private string? _tempIcoToCleanup;

    /// <summary>当前选定图标的渲染来源；用户自带图标文件时为 null</summary>
    private AppliedIconSource? _pendingSource;

    /// <summary>同步「隐藏小箭头」勾选状态时抑制事件</summary>
    private bool _suppressArrowToggle;

    // ============== 图标库侧边栏字段 ==============
    /// <summary>侧边栏里的一个图标（Fluent 与各 SVG 图标库共用）</summary>
    public class IconLibraryItem : INotifyPropertyChanged
    {
        public IconLibraryKind Library { get; set; }
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string MatchKey { get; set; } = string.Empty;

        /// <summary>仅 Fluent 有值；其余库为枚举默认值（不显示）</summary>
        public Icon FluentIcon { get; set; }

        private IconVariant _variant = IconVariant.Filled;
        public IconVariant Variant { get => _variant; set { _variant = value; OnPropertyChanged(); } }

        private Brush _foreground = Brushes.DimGray;
        public Brush Foreground { get => _foreground; set { _foreground = value; OnPropertyChanged(); } }

        private ImageSource? _thumb;
        /// <summary>非 Fluent 库的渲染缩略图</summary>
        public ImageSource? Thumb { get => _thumb; set { _thumb = value; OnPropertyChanged(); } }

        /// <summary>Fluent 图标控件的可见性</summary>
        public Visibility FluentVisibility =>
            Library == IconLibraryKind.Fluent ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>SVG 缩略图的可见性</summary>
        public Visibility ImageVisibility =>
            Library == IconLibraryKind.Fluent ? Visibility.Collapsed : Visibility.Visible;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private readonly List<IconLibraryItem> _allSidebarIcons = new();
    private readonly List<IconLibraryItem> _filteredSidebarIcons = new();
    private readonly ObservableCollection<IconLibraryItem> _visibleSidebarIcons = new();
    private int _loadedSidebarCount;                   // 当前已 Add 到 ItemsControl 的数量
    private const int FluentBatchSize = 50;            // 每次加载 50 个
    private const int SidebarThumbSize = 24;           // 侧边栏缩略图边长
    private IconVariant _currentFluentVariant = IconVariant.Filled;
    private IconLibraryItem? _selectedSidebarItem;
    private readonly DispatcherTimer _searchTimer;
    private bool _sidebarIsOpen;
    private bool _sidebarReady;

    // ============== 系统图标 / 文件类型字段 ==============
    private readonly ObservableCollection<ShellIconTarget> _systemTargets = new();
    private readonly ObservableCollection<ShellIconTarget> _fileTypeTargets = new();
    private bool _systemDataRequested;
    private bool _fileTypeDataRequested;

    // ============== 右上角轻量提示条 ==============
    private readonly DispatcherTimer _notifyTimer;
    private const int NotifyShowMs = 5000;

    // ============== 设置页：可选图标库 ==============
    private readonly ObservableCollection<IconPackItem> _iconPackItems = new();
    private bool _packsInitialized;
    private bool _suppressPackToggle;

    public MainWindow()
    {
        InitializeComponent();
        TitleBarTheme.Attach(this);
        lvIcons.ItemsSource = _items;
        lvSystemIcons.ItemsSource = _systemTargets;
        lvFileTypeIcons.ItemsSource = _fileTypeTargets;
        icIconPacks.ItemsSource = _iconPackItems;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;

        _notifyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(NotifyShowMs) };
        _notifyTimer.Tick += (_, _) => { _notifyTimer.Stop(); HideNotify(); };

        // 侧边栏初始化
        icFluentIcons.ItemsSource = _visibleSidebarIcons;
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplySidebarFilter(txtFluentSearch.Text); };

        InitLineColorControls();
        PopulateLibraries();

        // 线条颜色变更后，侧边栏缩略图与预览同步重渲染
        IconRenderOptions.Changed += OnRenderOptionsChanged;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        BtnScan_Click(sender, e);

        // 扫描完成后提示是否恢复历史
        Dispatcher.BeginInvoke(new Action(() => TryOfferHistoryRestore()),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void TryOfferHistoryRestore()
    {
        try
        {
            // 仅作非模态提示：不打断启动流程，恢复入口仍在工具栏「历史记录」。
            var alive = HistoryService.LoadAlive().ToList();
            if (alive.Count == 0) return;
            ShowNotify("有历史替换可用");
        }
        catch { /* 静默：历史读取失败不应该阻止启动 */ }
    }

    // ======================== 右上角轻量提示条 ========================

    /// <summary>
    /// 在窗口右上角显示一条非模态提示，约 5 秒后自动淡出。
    /// 不拦截输入（IsHitTestVisible=False），也不改变任何布局。
    /// </summary>
    private void ShowNotify(string text)
    {
        txtNotify.Text = text;

        brdNotify.BeginAnimation(OpacityProperty, null);
        ttNotify.BeginAnimation(TranslateTransform.YProperty, null);
        brdNotify.Opacity = 0;
        ttNotify.Y = -8;

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        brdNotify.BeginAnimation(OpacityProperty, fadeIn);

        var slideIn = new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ttNotify.BeginAnimation(TranslateTransform.YProperty, slideIn);

        _notifyTimer.Stop();
        _notifyTimer.Start();
    }

    private void HideNotify()
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        brdNotify.BeginAnimation(OpacityProperty, fadeOut);

        var slideOut = new DoubleAnimation(0, -8, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        ttNotify.BeginAnimation(TranslateTransform.YProperty, slideOut);
    }

    private void RestoreHistory()
    {
        var alive = HistoryService.LoadAlive().ToList();
        if (alive.Count == 0)
        {
            MessageBox.Show(this, "没有可恢复的历史记录（所有引用的图标文件都已被删除）。", "提示");
            return;
        }

        int ok = 0, fail = 0;
        foreach (var entry in alive)
        {
            if (!File.Exists(entry.FullPath)) continue; // 目标文件也没了
            var item = new DesktopIconItem
            {
                FullPath = entry.FullPath,
                DisplayName = entry.DisplayName,
                Extension = Path.GetExtension(entry.FullPath),
            };
            if (IconReplacer.ReplaceIcon(item, entry.IconPath, 0)) ok++;
            else fail++;
        }
        IconReplacer.RefreshIconCache();
        MessageBox.Show(this, $"恢复完成：成功 {ok} 项，失败 {fail} 项。", "完成");
        BtnScan_Click(this, new RoutedEventArgs());
    }

    private void BtnHistory_Click(object sender, RoutedEventArgs e)
    {
        var alive = HistoryService.LoadAlive().ToList();
        if (alive.Count == 0)
        {
            MessageBox.Show(this, "没有历史记录。\n\n每次成功的图标替换都会自动存一份快照到\n%APPDATA%\\DesktopIconChanger\\history.json", "历史记录");
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"共 {alive.Count} 条记录：");
        sb.AppendLine();
        int shown = 0;
        foreach (var g in alive.GroupBy(x => x.BatchId ?? "single"))
        {
            sb.AppendLine($"— {g.First().Timestamp:yyyy-MM-dd HH:mm:ss}（{g.Count()} 项）—");
            foreach (var h in g)
            {
                sb.AppendLine($"  • {h.DisplayName} → {Path.GetFileName(h.IconPath)}");
            }
            shown++;
            if (shown >= 8) { sb.AppendLine("  ... 更多省略"); break; }
        }

        var result = MessageBox.Show(this,
            sb.ToString() + $"\n\n持久化图标目录：{IconStorage.IconsDirectory}\n\n要重新应用全部历史吗？",
            $"历史记录 ({alive.Count} 项)", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (result == MessageBoxResult.Yes) RestoreHistory();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        IconRenderOptions.Changed -= OnRenderOptionsChanged;
        if (_tempIcoToCleanup != null) try { File.Delete(_tempIcoToCleanup); } catch { }
    }

    // ======================== 扫描 ========================

    private async void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        btnScan.IsEnabled = false;
        txtScanButton.Text = " 扫描中…";
        txtStatus.Text = "正在扫描桌面图标...";
        txtStatus.Foreground = (Brush)App.Current.FindResource("AccentWarn");

        var completion = new TaskCompletionSource<List<DesktopIconItem>>();
        var scanThread = new Thread(() =>
        {
            try
            {
                var items = DesktopScanner.ScanDesktop().ToList();
                foreach (var item in items)
                {
                    try { IconExtractor.LoadPreviewIcon(item); }
                    catch (Exception) { /* 单个图标解析失败则跳过，不中断整个扫描 */ }
                }
                completion.SetResult(items);
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        scanThread.SetApartmentState(ApartmentState.STA);
        scanThread.IsBackground = true;
        scanThread.Start();

        try
        {
            var items = await completion.Task;
            _items.Clear();
            foreach (var item in items) _items.Add(item);
            UpdateStatusBar();
            txtStatus.Text = $"扫描完成，共 {_items.Count} 项";
            txtStatus.Foreground = (Brush)App.Current.FindResource("AccentSuccess");
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"扫描失败：{ex.Message}";
            txtStatus.Foreground = (Brush)App.Current.FindResource("FgError");
        }
        finally { btnScan.IsEnabled = true; txtScanButton.Text = " 扫描桌面"; }
    }

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        var all = _items.All(i => i.IsSelected);
        foreach (var item in _items) item.IsSelected = !all;
        UpdateStatusBar();
    }

    private void ChkHeaderSelectAll_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox chk) foreach (var item in _items) item.IsSelected = chk.IsChecked == true;
        UpdateStatusBar();
    }

    // ======================== 原生图标文件（ICO/EXE/DLL/PNG/SVG...） ========================

    private async void BtnChooseIcon_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择图标文件",
            Filter = "所有支持的格式 (*.ico;*.png;*.svg;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff;*.webp)|*.ico;*.png;*.svg;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff;*.webp|" +
                     "图标/图标库 (*.ico;*.exe;*.dll)|*.ico;*.exe;*.dll|" +
                     "所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        var chosen = dialog.FileName;
        _selectedNewIconPath = chosen;
        _pendingSource = null;  // 用户自带文件，不属于任何图标库
        txtNewIconPath.Text = chosen;
        var ext = Path.GetExtension(chosen).ToLowerInvariant();

        if (ext == ".ico" || ext == ".exe" || ext == ".dll")
        {
            _resolvedIcoPath = chosen;
            var preview = ext == ".ico" ? IconExtractor.LoadIcoFile(chosen) : IconExtractor.GetIconFromFile(chosen, 0);
            imgReplacePreview.Source = preview;
            txtReplaceHint.Text = Path.GetFileName(chosen);
            txtStatus.Text = $"已选择图标：{Path.GetFileName(chosen)}";
            txtStatus.Foreground = (Brush)App.Current.FindResource("AccentBlue");
            return;
        }

        if (!IconConverter.IsSupportedInput(chosen))
        {
            MessageBox.Show(this, $"不支持的文件格式：{ext}\n支持的格式：ICO、PNG、SVG、JPG、BMP、GIF、TIFF、WEBP、EXE、DLL",
                "格式不支持", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BitmapSource? quickPreview = ext != ".svg" ? LoadImagePreview(chosen) : null;
        imgReplacePreview.Source = quickPreview;
        txtReplaceHint.Text = Path.GetFileName(chosen) + "（转换中…）";
        btnChooseIcon.IsEnabled = false;
        txtStatus.Text = $"正在将 {ext.ToUpperInvariant()} 转换为多尺寸 ICO...";
        txtStatus.Foreground = (Brush)App.Current.FindResource("AccentWarn");

        string? ico = null;
        try
        {
            var tint = IconRenderOptions.ResolveTint(App.IsDark);
            await System.Threading.Tasks.Task.Run(() => { ico = IconConverter.ConvertToTempIco(chosen, tint); });
        }
        catch (Exception ex) { txtStatus.Text = $"转换失败：{ex.Message}"; txtStatus.Foreground = (Brush)App.Current.FindResource("FgError"); }

        if (ico == null)
        {
            txtStatus.Text = "转换失败，请确认文件是否有效";
            txtStatus.Foreground = (Brush)App.Current.FindResource("FgError");
            btnChooseIcon.IsEnabled = true;
            return;
        }

        if (_tempIcoToCleanup != null && _tempIcoToCleanup != ico) try { File.Delete(_tempIcoToCleanup); } catch { }
        _tempIcoToCleanup = ico;
        _resolvedIcoPath = ico;
        var cp = IconExtractor.LoadIcoFile(ico);
        if (cp != null) imgReplacePreview.Source = cp;
        txtReplaceHint.Text = $"{Path.GetFileName(chosen)} → 已转换为多尺寸 ICO";
        txtStatus.Text = $"已选择 {Path.GetFileName(chosen)}（已转换为多尺寸 ICO）";
        txtStatus.Foreground = (Brush)App.Current.FindResource("AccentSuccess");
        btnChooseIcon.IsEnabled = true;
    }

    // ======================== 线条颜色 ========================

    private static Color ToColor(SkiaSharp.SKColor c) => Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void InitLineColorControls()
    {
        switch (IconRenderOptions.Mode)
        {
            case LineColorMode.KeepOriginal: rbColorOriginal.IsChecked = true; break;
            case LineColorMode.Custom: rbColorCustom.IsChecked = true; break;
            default: rbColorTheme.IsChecked = true; break;
        }
        txtCustomHex.Text = IconRenderOptions.CustomHex;
    }

    private void LineColorMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_sidebarReady) return;
        if (rbColorOriginal.IsChecked == true) IconRenderOptions.Mode = LineColorMode.KeepOriginal;
        else if (rbColorTheme.IsChecked == true) IconRenderOptions.Mode = LineColorMode.FollowTheme;
        else IconRenderOptions.Mode = LineColorMode.Custom;
    }

    private void ColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex }) return;
        txtCustomHex.Text = hex;
        IconRenderOptions.CustomHex = hex;
        rbColorCustom.IsChecked = true;
    }

    private void TxtCustomHex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyCustomHex();
    }

    private void TxtCustomHex_LostFocus(object sender, RoutedEventArgs e) => ApplyCustomHex();

    private void ApplyCustomHex()
    {
        var hex = txtCustomHex.Text?.Trim() ?? string.Empty;
        if (IconRenderOptions.ParseHex(hex) == null)
        {
            // 非法输入回退到当前值，不改变设置
            txtCustomHex.Text = IconRenderOptions.CustomHex;
            return;
        }
        txtCustomHex.Text = IconRenderOptions.CustomHex;
        IconRenderOptions.CustomHex = hex;
        rbColorCustom.IsChecked = true;
    }

    private void OnRenderOptionsChanged() => RefreshTint();

    /// <summary>线条颜色变更后：重渲染侧边栏缩略图、Fluent 前景色与底部预览。</summary>
    private void RefreshTint()
    {
        var fluentBrush = SidebarFluentBrush();
        foreach (var item in _allSidebarIcons)
        {
            if (item.Library == IconLibraryKind.Fluent) item.Foreground = fluentBrush;
            else item.Thumb = null; // 换色后旧的缩略图作废（缓存按颜色区分 key）
        }

        foreach (var item in _visibleSidebarIcons)
            if (item.Library != IconLibraryKind.Fluent) item.Thumb = RenderThumb(item, SidebarThumbSize);

        if (_selectedSidebarItem != null) ShowSidebarSelection(_selectedSidebarItem);
    }

    /// <summary>Fluent 图标在侧边栏里的前景色（Fluent 是字形，本身没有内建颜色）。</summary>
    private static Brush SidebarFluentBrush()
    {
        if (IconRenderOptions.Mode == LineColorMode.Custom &&
            IconRenderOptions.ParseHex(IconRenderOptions.CustomHex) is { } c)
            return Frozen(ToColor(c));
        return (Brush)App.Current.FindResource("FgMain");
    }

    // ======================== 图标库侧边栏 ========================

    /// <summary>重建图标库下拉框。安装 / 卸载 / 启用状态变化后需要重来一遍。</summary>
    private void PopulateLibraries()
    {
        _sidebarReady = false;
        cmbIconLibrary.Items.Clear();

        foreach (var kind in IconLibraryRegistry.Available())
        {
            cmbIconLibrary.Items.Add(new ComboBoxItem
            {
                Content = IconLibraryRegistry.DisplayName(kind),
                Tag = kind,
            });
        }

        _sidebarReady = true;
        if (cmbIconLibrary.Items.Count > 0) cmbIconLibrary.SelectedIndex = 0;
    }

    private void CmbIconLibrary_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_sidebarReady) return;
        if (cmbIconLibrary.SelectedItem is not ComboBoxItem { Tag: IconLibraryKind kind }) return;
        LoadLibrary(kind);
    }

    private void LoadLibrary(IconLibraryKind kind)
    {
        _selectedSidebarItem = null;
        btnUseFluentIcon.IsEnabled = false;
        txtSidebarSelName.Text = "（未选择）";
        txtSidebarSelVariant.Text = string.Empty;

        _allSidebarIcons.Clear();
        var fluentBrush = SidebarFluentBrush();
        foreach (var entry in IconLibraryRegistry.All(kind))
        {
            _allSidebarIcons.Add(new IconLibraryItem
            {
                Library = entry.Library,
                Id = entry.Id,
                Name = entry.Name,
                MatchKey = entry.MatchKey,
                FluentIcon = entry.FluentIcon ?? default,
                Variant = _currentFluentVariant,
                Foreground = fluentBrush,
            });
        }

        txtSidebarTitle.Text = " " + IconLibraryRegistry.DisplayName(kind);
        pnlVariant.Visibility = kind == IconLibraryKind.Fluent ? Visibility.Visible : Visibility.Collapsed;

        ApplySidebarFilter(txtFluentSearch.Text);
    }

    private void ResetSidebarPaging()
    {
        _visibleSidebarIcons.Clear();
        _loadedSidebarCount = 0;
        LoadMoreSidebarIcons();
        UpdateSidebarCount();
    }

    private static BitmapSource? PngToImage(byte[]? png)
    {
        if (png == null || png.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(png);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static BitmapSource? RenderThumb(IconLibraryItem item, int size)
    {
        if (item.Library == IconLibraryKind.Fluent) return null;
        var png = IconThumbnailCache.GetOrRender(item.Library, item.Id, size,
            IconRenderOptions.ResolveTint(App.IsDark));
        return PngToImage(png);
    }

    private void LoadMoreSidebarIcons()
    {
        var remaining = _filteredSidebarIcons.Count - _loadedSidebarCount;
        var take = Math.Min(FluentBatchSize, remaining);
        if (take <= 0) { txtFluentMoreStatus.Text = "已全部加载"; return; }

        for (int i = 0; i < take; i++)
        {
            var item = _filteredSidebarIcons[_loadedSidebarCount + i];
            if (item.Library != IconLibraryKind.Fluent && item.Thumb == null)
                item.Thumb = RenderThumb(item, SidebarThumbSize);
            _visibleSidebarIcons.Add(item);
        }
        _loadedSidebarCount += take;

        txtFluentMoreStatus.Text = _loadedSidebarCount >= _filteredSidebarIcons.Count
            ? $"已全部加载 {_loadedSidebarCount} 个"
            : $"已加载 {_loadedSidebarCount}/{_filteredSidebarIcons.Count} 个 · 滚动到底部加载更多";
    }

    private void SvFluentIcons_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange <= 0) return;
        // 接近底部时加载更多
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 20)
        {
            LoadMoreSidebarIcons();
        }
    }

    private void TxtFluentSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ApplySidebarFilter(string? keyword)
    {
        _filteredSidebarIcons.Clear();

        var kw = (keyword ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(kw))
        {
            _filteredSidebarIcons.AddRange(_allSidebarIcons);
        }
        else
        {
            foreach (var item in _allSidebarIcons)
            {
                if (IconSearchKeywords.Matches(item.MatchKey, kw)) _filteredSidebarIcons.Add(item);
            }
        }

        ResetSidebarPaging();
        svFluentIcons.ScrollToTop();
    }

    private void RbVariant_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _currentFluentVariant = rbVariantRegular.IsChecked == true ? IconVariant.Regular
                              : rbVariantFilled.IsChecked == true ? IconVariant.Filled
                              : IconVariant.Light;
        foreach (var it in _allSidebarIcons) it.Variant = _currentFluentVariant;
        if (_selectedSidebarItem != null)
        {
            icSidebarPreview.IconVariant = _selectedSidebarItem.Variant;
            txtSidebarSelVariant.Text = _selectedSidebarItem.Variant.ToString();
        }
    }

    private void UpdateSidebarCount()
    {
        txtFluentCount.Text = $"共 {_filteredSidebarIcons.Count} 个";
    }

    private void IconItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.DataContext is IconLibraryItem item)
        {
            ShowSidebarSelection(item);
        }
    }

    private void ShowSidebarSelection(IconLibraryItem item)
    {
        _selectedSidebarItem = item;
        txtSidebarSelName.Text = item.Name;
        btnUseFluentIcon.IsEnabled = true;

        if (item.Library == IconLibraryKind.Fluent)
        {
            icSidebarPreview.Visibility = Visibility.Visible;
            imgSidebarPreview.Visibility = Visibility.Collapsed;
            icSidebarPreview.Icon = item.FluentIcon;
            icSidebarPreview.IconVariant = item.Variant;
            icSidebarPreview.Foreground = item.Foreground;
            txtSidebarSelVariant.Text = item.Variant.ToString();
        }
        else
        {
            icSidebarPreview.Visibility = Visibility.Collapsed;
            imgSidebarPreview.Visibility = Visibility.Visible;
            imgSidebarPreview.Source = RenderThumb(item, 40) ?? item.Thumb;
            txtSidebarSelVariant.Text = IconLibraryRegistry.DisplayName(item.Library);
        }
    }

    private void BtnUseFluentIcon_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSidebarItem == null) return;
        if (_selectedSidebarItem.Library == IconLibraryKind.Fluent)
            ApplyFluentIconToMain(_selectedSidebarItem.FluentIcon, _selectedSidebarItem.Variant);
        else
            ApplySvgIconToMain(_selectedSidebarItem);

        ToggleFluentSidebar();  // 收起侧边栏
    }

    /// <summary>
    /// 把「线条颜色」设置解析成 WPF 画刷；返回 null 表示保持图标原色。
    /// </summary>
    private static Brush? CurrentTintBrush()
    {
        if (IconRenderOptions.ResolveTint(App.IsDark) is not { } c) return null;
        return Frozen(ToColor(c));
    }

    private void ApplyFluentIconToMain(Icon icon, IconVariant variant)
    {
        try
        {
            txtStatus.Text = $"正在渲染 Fluent 图标 {icon}…";
            txtStatus.Foreground = (Brush)App.Current.FindResource("AccentWarn");

            var rendered = FluentIconRenderer.Render(icon, variant, 256, CurrentTintBrush());
            var ico = IconConverter.ConvertBitmapSourceToTempIco(rendered);

            // 记下渲染来源，之后才能在 Filled / Regular 之间一键互转
            _pendingSource = new AppliedIconSource
            {
                Library = nameof(IconLibraryKind.Fluent),
                IconId = icon.ToString(),
                Variant = variant.ToString(),
                TintHex = IconRenderOptions.ResolveTintHex(App.IsDark),
            };

            if (ico == null)
            {
                txtStatus.Text = "ICO 转换失败";
                txtStatus.Foreground = (Brush)App.Current.FindResource("FgError");
                return;
            }

            if (_tempIcoToCleanup != null && _tempIcoToCleanup != ico) try { File.Delete(_tempIcoToCleanup); } catch { }
            _tempIcoToCleanup = ico;
            _resolvedIcoPath = ico;
            _selectedNewIconPath = null;

            txtNewIconPath.Text = $"Fluent 图标：{icon} ({variant})";
            var preview = IconExtractor.LoadIcoFile(ico);
            if (preview != null) imgReplacePreview.Source = preview;
            txtReplaceHint.Text = $"{icon} ({variant}) → 已转换为多尺寸 ICO";

            txtStatus.Text = $"已从 Fluent 图标库选择：{icon} ({variant})";
            txtStatus.Foreground = (Brush)App.Current.FindResource("AccentSuccess");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"操作失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>把某个 SVG 图标库的图标渲染成多尺寸 ICO 并设为当前选定图标。</summary>
    private void ApplySvgIconToMain(IconLibraryItem item)
    {
        try
        {
            var svg = IconLibraryRegistry.GetSvg(item.Library, item.Id);
            if (svg == null)
            {
                MessageBox.Show(this, "该图标的源文件不可用（可能已在编译时裁掉该图标库）。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            txtStatus.Text = $"正在渲染 {item.Name}…";
            txtStatus.Foreground = (Brush)App.Current.FindResource("AccentWarn");

            var ico = IconConverter.ConvertSvgBytesToTempIco(svg, IconRenderOptions.ResolveTint(App.IsDark));

            // 记下渲染来源（SVG 图标库没有 Fill/Regular 之分，变体留空）
            _pendingSource = new AppliedIconSource
            {
                Library = item.Library.ToString(),
                IconId = item.Id,
                Variant = null,
                TintHex = IconRenderOptions.ResolveTintHex(App.IsDark),
            };

            if (ico == null)
            {
                txtStatus.Text = "ICO 转换失败";
                txtStatus.Foreground = (Brush)App.Current.FindResource("FgError");
                return;
            }

            if (_tempIcoToCleanup != null && _tempIcoToCleanup != ico) try { File.Delete(_tempIcoToCleanup); } catch { }
            _tempIcoToCleanup = ico;
            _resolvedIcoPath = ico;
            _selectedNewIconPath = null;

            var libraryName = IconLibraryRegistry.DisplayName(item.Library);
            txtNewIconPath.Text = $"图标库：{libraryName} / {item.Name}";
            var preview = IconExtractor.LoadIcoFile(ico);
            if (preview != null) imgReplacePreview.Source = preview;
            txtReplaceHint.Text = $"{item.Name} → 已转换为多尺寸 ICO";

            txtStatus.Text = $"已从 {libraryName} 选择：{item.Name}";
            txtStatus.Foreground = (Brush)App.Current.FindResource("AccentSuccess");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"操作失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnToggleFluentSidebar_Click(object sender, RoutedEventArgs e) => ToggleFluentSidebar();
    private void BtnCloseSidebar_Click(object sender, RoutedEventArgs e) => ToggleFluentSidebar();

    private void ToggleFluentSidebar()
    {
        _sidebarIsOpen = !_sidebarIsOpen;
        colFluentSidebar.Width = _sidebarIsOpen ? new GridLength(380) : new GridLength(0);
        txtToggleSidebar.Text = _sidebarIsOpen ? " 收起图标库" : " 图标库";

        if (_sidebarIsOpen && _loadedSidebarCount == 0 && _filteredSidebarIcons.Count > 0)
        {
            // 首次展开：加载前 50 个
            ResetSidebarPaging();
        }
    }

    // ======================== 目标视图切换 ========================

    private void TargetView_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        var system = rbViewSystem.IsChecked == true;
        var fileType = rbViewFileType.IsChecked == true;
        var settings = rbViewSettings.IsChecked == true;

        pnlDesktopView.Visibility = !system && !fileType && !settings ? Visibility.Visible : Visibility.Collapsed;
        pnlSystemView.Visibility = system ? Visibility.Visible : Visibility.Collapsed;
        pnlFileTypeView.Visibility = fileType ? Visibility.Visible : Visibility.Collapsed;
        pnlSettingsView.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;

        txtViewHint.Text = system
            ? "只改图标，写入当前用户注册表"
            : fileType ? "只改图标，不影响文件打开方式"
            : settings ? "图标库按需下载，基础包不内置" : string.Empty;

        if (system) EnsureShellData(fileType: false);
        else if (fileType) EnsureShellData(fileType: true);
        else if (settings) EnsureIconPackData();

        // 切换视图后原来的选中项不再适用，清空右侧预览
        if (system || fileType)
        {
            imgPreview.Source = null;
            imgTargetIcon.Source = null;
            txtPreviewName.Text = string.Empty;
            txtPreviewPath.Text = string.Empty;
            txtPreviewSource.Text = "请选择一项以查看图标来源";
        }

        UpdateStatusBar();
    }

    // ======================== 设置页：可选图标库 ========================

    /// <summary>首次进入设置页时构建列表，之后只刷新状态。</summary>
    private void EnsureIconPackData()
    {
        if (!_packsInitialized)
        {
            _packsInitialized = true;
            foreach (var info in IconPackCatalog.All)
            {
                _iconPackItems.Add(new IconPackItem
                {
                    Kind = info.Kind,
                    DisplayName = info.DisplayName,
                });
            }
        }

        foreach (var item in _iconPackItems) ApplyIconPackState(item);

        EnsureVariantToggleState();
        EnsureShortcutArrowState();
    }

    // ======================== 设置页：已替换图标 / 资源管理器外观 ========================

    private void EnsureVariantToggleState()
    {
        var count = VariantToggleService.CountToggleable();
        btnToggleVariant.IsEnabled = count > 0;
        txtVariantToggleHint.Text = count > 0
            ? $"当前有 {count} 个图标可在 Filled / Regular 之间互转"
            : "暂无可切换的图标：只有本版本替换过、且来源为 Fluent 图标的项目才能互转；旧版本的替换没有记录图标来源，用一个 Fluent 图标重新替换一次即可参与互转。";
    }

    /// <summary>按记录的渲染来源重新生成多尺寸 ICO；失败返回 null。</summary>
    private static string? RenderSourceToIco(AppliedIconSource source)
    {
        if (!Enum.TryParse<FluentIcons.Common.Icon>(source.IconId, out var icon)) return null;
        if (!Enum.TryParse<IconVariant>(source.Variant, out var variant)) return null;

        var brush = IconRenderOptions.ParseHex(source.TintHex) is { } c ? Frozen(ToColor(c)) : null;
        var rendered = FluentIconRenderer.Render(icon, variant, 256, brush);
        return IconConverter.ConvertBitmapSourceToTempIco(rendered);
    }

    private async void BtnToggleVariant_Click(object sender, RoutedEventArgs e)
    {
        var count = VariantToggleService.CountToggleable();
        if (count == 0)
        {
            ShowNotify("没有可切换的图标");
            EnsureVariantToggleState();
            return;
        }

        var confirm = MessageBox.Show(this,
            $"将把 {count} 个已替换的 Fluent 图标在 Filled / Regular 之间互换。\n\n" +
            "图标会按记录时的线条颜色重新渲染后再写回原位置。\n" +
            "系统图标 / 文件类型 / 文件夹可能需要重启资源管理器才能看到效果。",
            "切换 Fill / Regular 形态", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        btnToggleVariant.IsEnabled = false;
        txtStatus.Text = "正在切换图标形态...";
        txtStatus.Foreground = (Brush)App.Current.FindResource("AccentWarn");

        try
        {
            var result = await VariantToggleService.ToggleAsync(
                src => System.Threading.Tasks.Task.FromResult(RenderSourceToIco(src)));

            txtStatus.Text = result.Total > 0
                ? $"已切换 {result.Total} 个图标的形态"
                : "没有图标被切换";
            txtStatus.Foreground = result.Total > 0
                ? (Brush)App.Current.FindResource("AccentSuccess")
                : (Brush)App.Current.FindResource("AccentWarn");

            var detail = result.Skipped > 0 ? $"\n{result.Skipped} 项因图标库不可用被跳过。" : string.Empty;
            ShowNotify(result.Total > 0 ? $"已切换 {result.Total} 个图标的形态" : "没有图标被切换");

            MessageBox.Show(this,
                $"切换完成。\n\n桌面 / 任务栏：{result.DesktopItems} 项\n" +
                $"系统图标 / 文件类型 / 文件夹：{result.ShellTargets} 项{detail}\n\n" +
                "若图标未立即更新，请点击「刷新缓存」或重启资源管理器。",
                "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            btnToggleVariant.IsEnabled = true;
            EnsureVariantToggleState();
            BtnScan_Click(this, new RoutedEventArgs());
        }
    }

    private void ChkHideShortcutArrow_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressArrowToggle) return;

        if (chkHideShortcutArrow.IsChecked == true)
        {
            var ico = IconConverter.CreateTransparentTempIco();
            if (ico == null)
            {
                ShowNotify("无法生成透明图标");
                EnsureShortcutArrowState();
                return;
            }

            string permanent;
            try { permanent = IconStorage.EnsurePermanent(ico); }
            catch { permanent = ico; }

            if (ShellIconService.Apply(ShellIconService.ShortcutArrowTarget(), permanent, 0))
            {
                txtStatus.Text = "已隐藏快捷方式小箭头";
                txtStatus.Foreground = (Brush)App.Current.FindResource("AccentSuccess");
                ShowNotify("已隐藏小箭头");
                OfferExplorerRestart("小箭头已隐藏（HKCU / HKLM 的 Shell Icons\\29 已指向透明图标）。");
            }
            else
            {
                ShowNotify("隐藏小箭头失败");
                EnsureShortcutArrowState();
                return;
            }
        }
        else
        {
            if (ShellIconService.Restore(ShellIconService.ShortcutArrowTarget()))
            {
                txtStatus.Text = "已恢复快捷方式小箭头";
                txtStatus.Foreground = (Brush)App.Current.FindResource("AccentSuccess");
                ShowNotify("已恢复快捷方式小箭头");
                OfferExplorerRestart("小箭头已恢复为系统默认。");
            }
            else
            {
                ShowNotify("恢复小箭头失败");
                EnsureShortcutArrowState();
                return;
            }
        }

        UpdateShortcutArrowHint();
    }

    /// <summary>把「隐藏小箭头」的实际状态同步到勾选框上。</summary>
    private void EnsureShortcutArrowState()
    {
        _suppressArrowToggle = true;
        try { chkHideShortcutArrow.IsChecked = ShellIconService.IsApplied("ShortcutArrow"); }
        finally { _suppressArrowToggle = false; }

        UpdateShortcutArrowHint();
    }

    private void UpdateShortcutArrowHint()
        => txtShortcutArrowHint.Text = ShellIconService.IsApplied("ShortcutArrow")
            ? "当前：已隐藏（重启资源管理器后生效）"
            : "当前：系统默认";

    /// <summary>Shell Icons 改动要重启资源管理器才生效，直接问用户是否现在重启。</summary>
    private void OfferExplorerRestart(string message)
    {
        var result = MessageBox.Show(this,
            message + "\n\n资源管理器需要重启一次才能看到效果，是否立即重启？",
            "使其生效", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        txtStatus.Text = "正在重启资源管理器...";
        txtStatus.Foreground = (Brush)App.Current.FindResource("AccentWarn");
        IconReplacer.RestartExplorer();
        txtStatus.Text = "资源管理器已重启";
        txtStatus.Foreground = (Brush)App.Current.FindResource("AccentSuccess");
    }

    /// <summary>把安装 / 启用状态同步到行模型上。</summary>
    private void ApplyIconPackState(IconPackItem item)
    {
        var installed = IconPackInstaller.IsInstalled(item.Kind);

        _suppressPackToggle = true;
        try
        {
            item.IsInstalled = installed;
            item.PackEnabled = IconPackInstaller.IsEnabled(item.Kind);
        }
        finally
        {
            _suppressPackToggle = false;
        }

        item.HasError = false;
        item.ProgressPercent = 0;

        if (!installed)
        {
            item.StatusText = "未安装";
        }
        else
        {
            var size = FormatSize(IconPackInstaller.InstalledSize(item.Kind));
            item.StatusText = item.PackEnabled ? $"已安装（{size}）" : $"已安装（{size}），已禁用";
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
        return (bytes / (1024.0 * 1024.0)).ToString("0.#") + " MB";
    }

    private async void BtnPackDownload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: IconPackItem item }) return;
        if (item.IsInstalled || item.IsBusy) return;

        item.IsBusy = true;
        item.HasError = false;
        item.ProgressPercent = 0;
        item.StatusText = "正在下载 0%";

        var progress = new Progress<double>(p =>
        {
            item.ProgressPercent = p * 100;
            item.StatusText = $"正在下载 {p * 100:0}%";
        });

        try
        {
            await IconPackInstaller.InstallAsync(item.Kind, progress, CancellationToken.None);
            item.IsBusy = false;
            ApplyIconPackState(item);
            PopulateLibraries();
            ShowNotify($"「{item.DisplayName}」已安装并启用");
        }
        catch (Exception ex)
        {
            item.IsBusy = false;
            item.HasError = true;
            item.ProgressPercent = 0;
            item.StatusText = "下载失败：" + IconPackInstaller.Describe(ex);
            ShowNotify($"「{item.DisplayName}」下载失败");
        }
    }

    private void BtnPackRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: IconPackItem item }) return;
        if (!item.IsInstalled || item.IsBusy) return;

        var confirm = MessageBox.Show(this,
            $"确定删除「{item.DisplayName}」的图标库文件吗？\n\n删除后该库将从图标库列表中移除，可随时重新下载。",
            "删除图标库", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        IconPackInstaller.Uninstall(item.Kind);
        ApplyIconPackState(item);
        PopulateLibraries();
        ShowNotify($"已删除「{item.DisplayName}」");
    }

    private void ChkPackEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressPackToggle) return;
        if (sender is not CheckBox checkBox || checkBox.DataContext is not IconPackItem item) return;

        var enabled = checkBox.IsChecked == true;
        item.PackEnabled = enabled;

        IconPackInstaller.SetEnabled(item.Kind, enabled);
        ApplyIconPackState(item);
        PopulateLibraries();
        ShowNotify(enabled ? $"已启用「{item.DisplayName}」" : $"已禁用「{item.DisplayName}」");
    }

    private void BtnPackLicense_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: IconPackItem item }) return;

        var text = IconLibraryRegistry.LicenseText(item.Kind);
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show(this, "未找到该图标库的许可文本。", item.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // MessageBox 不支持滚动，长许可协议会被裁；改用可滚动的文本对话框。
        new TextDialogWindow(text, $"{item.DisplayName} 许可协议") { Owner = this }.ShowDialog();
    }

    private async void EnsureShellData(bool fileType)
    {
        if (fileType ? _fileTypeDataRequested : _systemDataRequested) return;
        if (fileType) _fileTypeDataRequested = true; else _systemDataRequested = true;

        var completion = new TaskCompletionSource<List<ShellIconTarget>>();
        var thread = new Thread(() =>
        {
            try
            {
                var list = (fileType ? ShellIconService.FileTypeTargets() : ShellIconService.SystemTargets()).ToList();
                ShellIconService.RefreshState(list);
                completion.SetResult(list);
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        try
        {
            var list = await completion.Task;
            var collection = fileType ? _fileTypeTargets : _systemTargets;
            collection.Clear();
            foreach (var target in list) collection.Add(target);
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"读取系统图标失败：{ex.Message}";
            txtStatus.Foreground = (Brush)App.Current.FindResource("FgError");
        }
    }

    private void ShellTarget_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var target = (sender as ListView)?.SelectedItem as ShellIconTarget;
        if (target == null) return;

        if (target.Kind == ShellTargetKind.FileExtension && target.Extension != null)
            txtFileExtension.Text = target.Extension;

        imgPreview.Source = target.PreviewIcon;
        imgTargetIcon.Source = imgReplacePreview.Source;
        txtPreviewName.Text = target.Extension == null
            ? target.DisplayName
            : $"{target.DisplayName}（{target.Extension}）";
        txtPreviewPath.Text = "HKCU\\" + ShellIconService.DescribeKeyPath(target);
        txtPreviewSource.Text = string.IsNullOrEmpty(target.CurrentIcon)
            ? (target.IsCustomized ? "已自定义" : "系统默认图标")
            : target.CurrentIcon!;
    }

    private void RefreshShellTarget(ShellIconTarget target)
    {
        ShellIconService.RefreshState(target);
        if (target.Kind == ShellTargetKind.FileExtension) lvFileTypeIcons.Items.Refresh();
        else lvSystemIcons.Items.Refresh();

        imgPreview.Source = target.PreviewIcon;
    }

    private void BtnAddFileType_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveFileTypeTarget() == null)
            MessageBox.Show(this, "请输入有效的扩展名，例如 .txt 或 txt。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void TxtFileExtension_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BtnAddFileType_Click(sender, e);
    }

    /// <summary>
    /// 以扩展名输入框为准解析文件类型目标：已存在则复用，否则新建并加入列表。
    /// </summary>
    private ShellIconTarget? ResolveFileTypeTarget()
    {
        var ext = ShellIconService.NormalizeExtension(txtFileExtension.Text);
        if (ext == null) return null;

        var existing = _fileTypeTargets.FirstOrDefault(t => t.Extension == ext);
        if (existing != null)
        {
            lvFileTypeIcons.SelectedItem = existing;
            return existing;
        }

        var created = ShellIconService.CreateFileTypeTarget(ext);
        ShellIconService.RefreshState(created);
        _fileTypeTargets.Add(created);
        lvFileTypeIcons.SelectedItem = created;
        lvFileTypeIcons.ScrollIntoView(created);
        return created;
    }

    private void BtnRestoreShell_Click(object sender, RoutedEventArgs e)
    {
        var target = rbViewSystem.IsChecked == true
            ? lvSystemIcons.SelectedItem as ShellIconTarget
            : lvFileTypeIcons.SelectedItem as ShellIconTarget;

        if (target == null)
        {
            MessageBox.Show(this, "请先在列表中选择要恢复的项目。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!target.IsCustomized)
        {
            ShowNotify("该项目当前为系统默认，无需恢复");
            return;
        }

        var name = target.Extension ?? target.DisplayName;
        if (ShellIconService.Restore(target))
        {
            RefreshShellTarget(target);
            txtStatus.Text = $"已恢复「{name}」的默认图标";
            txtStatus.Foreground = (Brush)App.Current.FindResource("AccentSuccess");
            ShowNotify("已恢复默认图标");
        }
        else
        {
            txtStatus.Text = $"恢复「{name}」失败";
            txtStatus.Foreground = (Brush)App.Current.FindResource("FgError");
        }
    }

    // ======================== 应用替换 ========================

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (rbViewSystem.IsChecked == true)
        {
            if (lvSystemIcons.SelectedItem is not ShellIconTarget target)
            {
                MessageBox.Show(this, "请先选择要替换的系统图标。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ApplyToShellTarget(target);
            return;
        }

        if (rbViewFileType.IsChecked == true)
        {
            var target = ResolveFileTypeTarget();
            if (target == null)
            {
                MessageBox.Show(this, "请先输入或选择有效的扩展名。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ApplyToShellTarget(target);
            return;
        }

        ApplyToDesktopItems();
    }

    /// <summary>系统图标 / 文件类型：写入 HKCU 注册表并记录快照。</summary>
    private void ApplyToShellTarget(ShellIconTarget target)
    {
        if (_resolvedIcoPath == null || !File.Exists(_resolvedIcoPath))
        {
            MessageBox.Show(this, "请先选择要替换的图标文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string permanentIcoPath;
        try { permanentIcoPath = IconStorage.EnsurePermanent(_resolvedIcoPath); }
        catch { permanentIcoPath = _resolvedIcoPath; }

        var name = target.Extension ?? target.DisplayName;
        var result = MessageBox.Show(this,
            $"确定要替换「{name}」的图标吗？\n\n" +
            "该操作只写入当前用户注册表（HKCU），不影响文件打开方式，可通过「恢复默认」撤销。\n" +
            "若图标未立即更新，请点击「刷新缓存」或重启资源管理器。",
            "确认替换", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            if (ShellIconService.Apply(target, permanentIcoPath, 0, _pendingSource))
            {
                RefreshShellTarget(target);
                txtStatus.Text = $"已替换「{name}」的图标";
                txtStatus.Foreground = (Brush)App.Current.FindResource("AccentSuccess");
                ShowNotify($"「{name}」图标已替换");

                var restart = MessageBox.Show(this,
                    $"「{name}」图标已写入注册表。\n\n系统图标 / 文件类型图标通常需要重启资源管理器才能看到效果，是否立即重启？",
                    "替换成功", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (restart == MessageBoxResult.Yes)
                {
                    txtStatus.Text = "正在重启资源管理器...";
                    IconReplacer.RestartExplorer();
                    txtStatus.Text = $"已替换「{name}」的图标";
                }
            }
            else
            {
                txtStatus.Text = $"替换「{name}」失败（写入被拒绝）";
                txtStatus.Foreground = (Brush)App.Current.FindResource("FgError");
                MessageBox.Show(this, "写入注册表失败。请确认程序以管理员身份运行。", "替换失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            txtStatus.Text = $"替换出错：{ex.Message}";
            txtStatus.Foreground = (Brush)App.Current.FindResource("FgError");
        }
    }

    /// <summary>桌面项目：沿用原有快捷方式 / 注册表替换流程。</summary>
    private void ApplyToDesktopItems()
    {
        if (_resolvedIcoPath == null || !File.Exists(_resolvedIcoPath))
        {
            MessageBox.Show(this, "请先选择要替换的图标文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var selectedItems = _items.Where(i => i.IsSelected).ToList();
        if (selectedItems.Count == 0)
        {
            MessageBox.Show(this, "请先选择要替换图标的项目。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ============ 关键：把图标文件先存到持久目录 ============
        string permanentIcoPath;
        try
        {
            permanentIcoPath = IconStorage.EnsurePermanent(_resolvedIcoPath);
            // 如果源路径是临时文件（%TEMP%），EnsurePermanent 会复制到 %APPDATA% 下的稳定位置
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存图标到持久化目录失败：{ex.Message}\n\n" +
                "这可能导致重启后图标丢失，请检查写入权限。",
                "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            permanentIcoPath = _resolvedIcoPath; // 降级：继续用临时路径
        }

        var result = MessageBox.Show(this,
            $"确定要替换选中的 {selectedItems.Count} 个项目的图标吗？\n\n图标文件会保存至：\n%APPDATA%\\DesktopIconChanger\\Icons\n→ 重启后不会丢失。",
            "确认替换", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            txtStatus.Text = "正在替换图标...";
            txtStatus.Foreground = (Brush)App.Current.FindResource("AccentWarn");

            int successCount = 0, failCount = 0;
            var failDetails = new System.Text.StringBuilder();
            var successItems = new List<(DesktopIconItem, string)>();

            foreach (var item in selectedItems)
            {
                try
                {
                    if (IconReplacer.ReplaceIcon(item, permanentIcoPath, 0, _pendingSource))
                    {
                        successCount++;
                        successItems.Add((item, permanentIcoPath));
                        IconExtractor.LoadPreviewIcon(item);
                    }
                    else
                    {
                        failCount++;
                        failDetails.AppendLine($"• {item.DisplayName}：写入被拒绝（公共桌面/只读）");
                    }
                }
                catch (Exception ex) { failCount++; failDetails.AppendLine($"• {item.DisplayName}：{ex.Message}"); }
            }

            if (successItems.Count > 0)
            {
                // 写历史记录，下次启动可恢复
                HistoryService.AddBatch(successItems.Select(x => (x.Item1, x.Item2)), _pendingSource);
            }

            IconReplacer.RefreshIconCache();

            txtStatus.Text = $"替换完成：成功 {successCount} 项，失败 {failCount} 项（图标已持久化）";
            txtStatus.Foreground = failCount > 0
                ? (Brush)App.Current.FindResource("AccentWarn")
                : (Brush)App.Current.FindResource("AccentSuccess");

            var detail = failCount > 0
                ? $"\n\n失败详情：\n{failDetails}\n\n提示：公共桌面中的项目需要管理员权限。"
                : "";
            MessageBox.Show(this,
                $"替换完成。\n\n成功：{successCount} 项\n失败：{failCount} 项{detail}\n\n" +
                $"图标已保存至 %APPDATA%\\DesktopIconChanger\\Icons，重启后不会丢失。\n" +
                $"历史记录已保存，可在「历史记录」中查看。\n\n" +
                $"若桌面图标未立即更新，请点击「刷新缓存」或重启资源管理器。",
                "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"替换过程出错：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            txtStatus.Text = $"出错：{ex.Message}";
            txtStatus.Foreground = (Brush)App.Current.FindResource("FgError");
        }
    }

    private void BtnRefreshCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            txtStatus.Text = "正在刷新 Shell 图标缓存...";
            txtStatus.Foreground = (Brush)App.Current.FindResource("AccentWarn");
            IconReplacer.RefreshIconCache();
            BtnScan_Click(sender, e);
            MessageBox.Show(this,
                "图标缓存已刷新。若桌面图标仍未更新，\n请尝试重启资源管理器（任务管理器 → 找到「Windows 资源管理器」→ 右键 → 重新启动）。",
                "刷新完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { txtStatus.Text = $"刷新失败：{ex.Message}"; txtStatus.Foreground = (Brush)App.Current.FindResource("FgError"); }
    }

    // ======================== 列表选中变更 ========================

    private void LvIcons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = lvIcons.SelectedItem as DesktopIconItem;
        if (item == null)
        {
            imgPreview.Source = null;
            imgTargetIcon.Source = null;
            txtPreviewName.Text = "";
            txtPreviewPath.Text = "";
            txtPreviewSource.Text = "请选择一项以查看图标来源";
            return;
        }

        imgPreview.Source = item.PreviewIcon;
        imgTargetIcon.Source = item.TargetIcon;
        txtPreviewName.Text = item.DisplayName;
        txtPreviewPath.Text = item.FullPath;

        if (!string.IsNullOrEmpty(item.IconSourceDesc))
        {
            txtPreviewSource.Text = item.IconSourceDesc;
            txtPreviewSource.Foreground = item.IsShortcut
                ? (Brush)App.Current.FindResource("AccentBlue")
                : (Brush)App.Current.FindResource("FgSecondary");
        }
        else txtPreviewSource.Text = "";
    }

    private void LvIcons_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (lvIcons.SelectedItem is DesktopIconItem item)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.FullPath}\""); } catch { }
        }
    }

    // ======================== 辅助 ========================

    private static BitmapSource? LoadImagePreview(string imagePath)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    private void UpdateStatusBar()
    {
        if (rbViewSystem.IsChecked == true)
        {
            txtItemCount.Text = $"系统图标 {_systemTargets.Count} 项";
            txtSelectedCount.Text = lvSystemIcons.SelectedItem == null ? "未选择项目" : "已选择 1 项";
        }
        else if (rbViewFileType.IsChecked == true)
        {
            txtItemCount.Text = $"文件类型 {_fileTypeTargets.Count} 项";
            txtSelectedCount.Text = lvFileTypeIcons.SelectedItem == null ? "未选择项目" : "已选择 1 项";
        }
        else if (rbViewSettings.IsChecked == true)
        {
            var installed = _iconPackItems.Count(i => i.IsInstalled);
            txtItemCount.Text = $"可选图标库 {_iconPackItems.Count} 个";
            txtSelectedCount.Text = $"已安装 {installed} 个";
        }
        else
        {
            txtItemCount.Text = $"共 {_items.Count} 项";
            var selectedCount = _items.Count(i => i.IsSelected);
            txtSelectedCount.Text = $"已选择 {selectedCount} 项";
        }
    }
}