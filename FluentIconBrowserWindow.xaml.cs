using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FluentIcons.Common;

namespace DesktopIconChanger;

public partial class FluentIconBrowserWindow : Window
{
    /// <summary>浏览器内每个图标的显示条目</summary>
    public class IconItem : INotifyPropertyChanged
    {
        public Icon Icon { get; set; }
        public string Name { get; set; } = string.Empty;

        private IconVariant _variant = IconVariant.Filled;
        public IconVariant Variant
        {
            get => _variant;
            set { _variant = value; OnPropertyChanged(); }
        }

        private int _fontSize = 28;
        public int FontSize
        {
            get => _fontSize;
            set { _fontSize = value; OnPropertyChanged(); }
        }

        public Brush Foreground { get; } = Brushes.DimGray;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public Icon? SelectedIcon { get; private set; }
    public IconVariant SelectedVariant { get; private set; } = IconVariant.Filled;

    private readonly ObservableCollection<IconItem> _allItems = new();
    private readonly ObservableCollection<IconItem> _filteredItems = new();
    private readonly DispatcherTimer _searchTimer;

    public FluentIconBrowserWindow()
    {
        InitializeComponent();
        TitleBarTheme.Attach(this);
        icIcons.ItemsSource = _filteredItems;

        // 枚举 Icon 枚举全部 2933 个值
        foreach (var name in Enum.GetNames(typeof(Icon)))
        {
            if (!Enum.TryParse<Icon>(name, out var val)) continue;
            _allItems.Add(new IconItem { Icon = val, Name = name });
        }
        _filteredItems.Clear();
        foreach (var i in _allItems) _filteredItems.Add(i);

        // 搜索防抖
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ApplyFilter(txtSearch.Text);
        };
    }

    private void ApplyFilter(string? keyword)
    {
        _filteredItems.Clear();
        var k = (keyword ?? string.Empty).Trim().ToUpperInvariant();
        foreach (var it in _allItems)
        {
            if (string.IsNullOrEmpty(k) || it.Name.ToUpperInvariant().Contains(k))
                _filteredItems.Add(it);
        }
        svIcons.ScrollToTop();
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void CmbVariant_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var idx = cmbVariant.SelectedIndex;
        var variant = idx == 0 ? IconVariant.Regular
                     : idx == 1 ? IconVariant.Filled
                     : IconVariant.Light;
        SelectedVariant = variant;
        foreach (var it in _allItems) it.Variant = variant;
        if (icPreview != null) icPreview.IconVariant = variant;
    }

    private void CmbSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var idx = cmbSize.SelectedIndex;
        var size = idx switch { 0 => 24, 1 => 32, 2 => 40, 3 => 48, _ => 64 };
        foreach (var it in _allItems) it.FontSize = size;
    }

    private void IconItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.DataContext is IconItem item)
        {
            SelectedIcon = item.Icon;
            icPreview.Icon = item.Icon;
            txtSelectedName.Text = item.Name;
            txtSelectedVariant.Text = item.Variant.ToString();
            btnOk.IsEnabled = true;
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedIcon == null) return;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        SelectedIcon = null;
        DialogResult = false;
        Close();
    }
}
