using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CADImport.FaceModule;
using QuickLook.Common.Plugin;
using QuickLook.Plugin.DwgsViewer.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Drawing.Color;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Grid = System.Windows.Controls.Grid;

namespace QuickLook.Plugin.DwgsViewer
{
    public class DwgsViewerControl : Grid, IDisposable
    {
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private enum ViewMode
        {
            Grid,
            Detail
        }

        private enum FilterTab
        {
            All,
            Favorites,
            Frequent
        }

        private const int PageSize = 24;

        private readonly string _initialFilePath;
        private readonly ContextObject _context;
        private string _folderPath = string.Empty;

        private List<string> _allDrawingFiles = new List<string>();
        private List<string> _filteredFiles = new List<string>();
        private int _loadedCount = 0;
        private int _currentIndex = 0;
        private bool _isDarkBackground = true;
        private ViewMode _currentMode = ViewMode.Grid;
        private FilterTab _currentTab = FilterTab.All;

        // 缩略图后台生成队列与缓存
        private CancellationTokenSource? _thumbnailCts;
        private readonly object _queueLock = new object();
        private readonly Queue<string> _thumbnailQueue = new Queue<string>();
        private bool _isThumbnailWorkerRunning = false;
        private readonly Dictionary<string, ImageSource> _thumbnailCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        // Toast 浮动提示定时器
        private System.Windows.Threading.DispatcherTimer? _toastTimer;

        // Views
        private Grid _gridView = null!;
        private Grid _detailView = null!;
        private Border _toastBorder = null!;
        private TextBlock _txtToast = null!;

        // Grid View Controls
        private TextBlock _txtGridTitle = null!;
        private TextBlock _txtGridCount = null!;
        private Border _searchBorder = null!;
        private System.Windows.Shapes.Path _searchIcon = null!;
        private TextBlock _txtPlaceholder = null!;
        private TextBox _txtSearch = null!;
        private Button _btnClearSearch = null!;
        private ScrollViewer _scrollViewer = null!;
        private UniformGrid _uniformGrid = null!;
        private Button _btnGridTheme = null!;
        private Button _btnTabAll = null!;
        private Button _btnTabFav = null!;
        private Button _btnTabFreq = null!;
        private ComboBox _cmbColumns = null!;
        private TextBlock _txtCadStatus = null!;

        // Detail View Controls (原生矢量无极缩放渲染)
        private WindowsFormsHost _cadHost = null!;
        private CADPictureBox _cadPictBox = null!;
        private CADImaging _cadImaging = null!;
        private TextBlock _txtDetailFileName = null!;
        private Border _detailTopBarBorder = null!;
        private Border _detailBottomBorder = null!;
        private Button _btnBackToGrid = null!;
        private Button _btnPrev = null!;
        private Button _btnNext = null!;
        private TextBlock _txtDetailCounter = null!;
        private ComboBox _cmbSlides = null!;
        private Button _btnInsertCad = null!;
        private Button _btnFavDetail = null!;
        private Button _btnZoomOut = null!;
        private Button _btnZoomReset = null!;
        private Button _btnZoomIn = null!;
        private Button _btnDetailTheme = null!;

        // Theme Brushes
        private static readonly Brush DarkWindowBg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 26));
        private static readonly Brush LightWindowBg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 245, 247));
        private static readonly Brush DarkCardBg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 36, 38));
        private static readonly Brush LightCardBg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255));
        private static readonly Brush DarkCardHoverBg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 48, 52));
        private static readonly Brush LightCardHoverBg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 243, 248));
        private static readonly Brush DarkCardBorder = new SolidColorBrush(System.Windows.Media.Color.FromArgb(50, 255, 255, 255));
        private static readonly Brush LightCardBorder = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 0, 0, 0));
        private static readonly Brush DarkToolbarBg = new SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 32, 32, 34));
        private static readonly Brush LightToolbarBg = new SolidColorBrush(System.Windows.Media.Color.FromArgb(230, 255, 255, 255));
        private static readonly Brush TextPrimaryDark = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 240, 240));
        private static readonly Brush TextPrimaryLight = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30));
        private static readonly Brush TextSecondaryDark = new SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 160, 160));
        private static readonly Brush TextSecondaryLight = new SolidColorBrush(System.Windows.Media.Color.FromRgb(110, 110, 110));
        private static readonly Brush AccentBlueBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 215));
        private static readonly Brush GoldStarBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 185, 0));
        private static readonly Brush DarkSearchBg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 32, 35));
        private static readonly Brush LightSearchBg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 240, 243));
        private static readonly Brush DarkSearchFocusBg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 40, 45));
        private static readonly Brush LightSearchFocusBg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255));

        public DwgsViewerControl(string filePath, ContextObject context)
        {
            _initialFilePath = filePath;
            _context = context;
            _folderPath = Path.GetDirectoryName(filePath) ?? string.Empty;
            _isDarkBackground = ConfigManager.DarkTheme;

            Focusable = true;
            ClipToBounds = true;

            InitializeViews();

            UpdateViewVisibility();
            ApplyTheme();

            Loaded += (s, e) => Focus();
            MouseDown += (s, e) => Focus();
            PreviewMouseDown += OnPreviewMouseDown;
            PreviewKeyDown += OnPreviewKeyDown;

            CommandBindings.Add(new CommandBinding(NavigationCommands.BrowseBack, (s, e) =>
            {
                if (_currentMode == ViewMode.Detail)
                {
                    SwitchToGridView();
                    e.Handled = true;
                }
            }));
            CommandBindings.Add(new CommandBinding(NavigationCommands.BrowseForward, (s, e) =>
            {
                if (_currentMode == ViewMode.Grid)
                {
                    SwitchToDetailView(_currentIndex);
                    e.Handled = true;
                }
                else if (_currentMode == ViewMode.Detail)
                {
                    NextSlide();
                    e.Handled = true;
                }
            }));
        }

        private void InitializeViews()
        {
            // ================= Grid View (Gallery) =================
            _gridView = new Grid();
            _gridView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Top bar
            _gridView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Scroll area

            // Top Bar
            var topBarBorder = new Border
            {
                Padding = new Thickness(14, 8, 14, 8),
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = DarkCardBorder
            };

            var topBar = new Grid();
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Left Section: Folder Name & Filter Tabs
            var leftPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var iconText = new TextBlock
            {
                Text = "📐",
                FontSize = 15,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _txtGridTitle = new TextBlock
            {
                Text = string.IsNullOrEmpty(_folderPath) ? "当前目录" : Path.GetFileName(_folderPath),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = _folderPath
            };
            _txtGridCount = new TextBlock
            {
                Margin = new Thickness(6, 0, 12, 0),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };

            leftPanel.Children.Add(iconText);
            leftPanel.Children.Add(_txtGridTitle);
            leftPanel.Children.Add(_txtGridCount);

            _btnTabAll = CreateFilterTabButton("全部", FilterTab.All);
            _btnTabFav = CreateFilterTabButton("⭐ 收藏", FilterTab.Favorites);
            _btnTabFreq = CreateFilterTabButton("🔥 常用", FilterTab.Frequent);

            leftPanel.Children.Add(_btnTabAll);
            leftPanel.Children.Add(_btnTabFav);
            leftPanel.Children.Add(_btnTabFreq);

            Grid.SetColumn(leftPanel, 0);
            topBar.Children.Add(leftPanel);

            // Middle Section: Fluent Capsule Search Box
            _searchBorder = new Border
            {
                Width = 220,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(1),
                BorderBrush = DarkCardBorder,
                Background = DarkSearchBg,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 12, 0),
                Padding = new Thickness(10, 0, 8, 0),
                Cursor = Cursors.IBeam
            };
            _searchBorder.MouseDown += (s, e) =>
            {
                _txtSearch.Focus();
                e.Handled = true;
            };

            var searchGrid = new Grid();
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _searchIcon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M6,1 A5,5 0 1,0 6,11 A5,5 0 1,0 6,1 M9.5,9.5 L13.5,13.5"),
                Stroke = TextSecondaryDark,
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 14,
                Height = 14
            };
            Grid.SetColumn(_searchIcon, 0);
            searchGrid.Children.Add(_searchIcon);

            var inputGrid = new Grid();
            _txtPlaceholder = new TextBlock
            {
                Text = "搜索 DWG / DXF...",
                FontSize = 12,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI"),
                Foreground = TextSecondaryDark,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
                IsHitTestVisible = false
            };
            inputGrid.Children.Add(_txtPlaceholder);

            _txtSearch = new TextBox
            {
                FontSize = 12,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI"),
                Foreground = TextPrimaryDark,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                FocusVisualStyle = null
            };

            var template = new ControlTemplate(typeof(TextBox));
            var factory = new FrameworkElementFactory(typeof(ScrollViewer));
            factory.Name = "PART_ContentHost";
            factory.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            factory.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            template.VisualTree = factory;
            _txtSearch.Template = template;

            Action updateSearchState = () =>
            {
                bool hasText = !string.IsNullOrEmpty(_txtSearch.Text);
                bool isFocused = _txtSearch.IsKeyboardFocused;

                _txtPlaceholder.Visibility = (!hasText && !isFocused) ? Visibility.Visible : Visibility.Collapsed;
                _btnClearSearch.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;

                if (isFocused)
                {
                    _searchBorder.BorderBrush = AccentBlueBrush;
                    _searchBorder.Background = _isDarkBackground ? DarkSearchFocusBg : LightSearchFocusBg;
                }
                else
                {
                    _searchBorder.BorderBrush = _isDarkBackground ? DarkCardBorder : LightCardBorder;
                    _searchBorder.Background = _isDarkBackground ? DarkSearchBg : LightSearchBg;
                }
            };

            _txtSearch.TextChanged += (s, e) =>
            {
                updateSearchState();
                FilterDrawings();
            };
            _txtSearch.GotKeyboardFocus += (s, e) => updateSearchState();
            _txtSearch.LostKeyboardFocus += (s, e) => updateSearchState();
            _txtSearch.PreviewKeyDown += (s, e) => _txtPlaceholder.Visibility = Visibility.Collapsed;

            inputGrid.Children.Add(_txtSearch);
            Grid.SetColumn(inputGrid, 1);
            searchGrid.Children.Add(inputGrid);

            _btnClearSearch = new Button
            {
                Content = "✕",
                ToolTip = "清空搜索",
                Background = Brushes.Transparent,
                Foreground = TextSecondaryDark,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 0, 2, 0),
                Cursor = Cursors.Hand,
                FontSize = 10,
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            _btnClearSearch.Click += (s, e) =>
            {
                _txtSearch.Text = "";
                _txtSearch.Focus();
            };
            Grid.SetColumn(_btnClearSearch, 2);
            searchGrid.Children.Add(_btnClearSearch);

            _searchBorder.Child = searchGrid;
            Grid.SetColumn(_searchBorder, 1);
            topBar.Children.Add(_searchBorder);

            // Right Section: Columns Selector + CAD Status + Theme
            var rightPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            _txtCadStatus = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = Cursors.Hand,
                ToolTip = "点击检测正在运行的 CAD"
            };
            _txtCadStatus.MouseDown += (s, e) => RefreshCadStatus(showToast: true);
            rightPanel.Children.Add(_txtCadStatus);

            var lblCols = new TextBlock
            {
                Text = "列数:",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                Foreground = TextSecondaryDark
            };
            rightPanel.Children.Add(lblCols);

            _cmbColumns = new ComboBox
            {
                Width = 64,
                Height = 24,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Focusable = false
            };
            _cmbColumns.Items.Add("自动");
            _cmbColumns.Items.Add("4 列");
            _cmbColumns.Items.Add("5 列");
            _cmbColumns.Items.Add("6 列");
            _cmbColumns.Items.Add("8 列");

            int savedCols = ConfigManager.Columns;
            if (savedCols == 4) _cmbColumns.SelectedIndex = 1;
            else if (savedCols == 5) _cmbColumns.SelectedIndex = 2;
            else if (savedCols == 6) _cmbColumns.SelectedIndex = 3;
            else if (savedCols == 8) _cmbColumns.SelectedIndex = 4;
            else _cmbColumns.SelectedIndex = 0;

            _cmbColumns.SelectionChanged += (s, e) =>
            {
                int newCols = 0;
                switch (_cmbColumns.SelectedIndex)
                {
                    case 1: newCols = 4; break;
                    case 2: newCols = 5; break;
                    case 3: newCols = 6; break;
                    case 4: newCols = 8; break;
                    default: newCols = 0; break;
                }
                ConfigManager.Columns = newCols;
                UpdateGridLayout(_scrollViewer.ViewportWidth > 0 ? _scrollViewer.ViewportWidth : ActualWidth);
            };
            rightPanel.Children.Add(_cmbColumns);

            _btnGridTheme = CreateToolbarButton("🌓", "切换底色 (B)");
            _btnGridTheme.Click += (s, e) => ToggleTheme();
            rightPanel.Children.Add(_btnGridTheme);

            Grid.SetColumn(rightPanel, 2);
            topBar.Children.Add(rightPanel);

            topBarBorder.Child = topBar;
            Grid.SetRow(topBarBorder, 0);
            _gridView.Children.Add(topBarBorder);

            // Scroll Area with UniformGrid
            _scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(14, 12, 14, 16)
            };

            _uniformGrid = new UniformGrid
            {
                Columns = 5,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _scrollViewer.Content = _uniformGrid;

            _scrollViewer.ScrollChanged += (s, e) =>
            {
                if (e.ViewportWidthChange != 0)
                {
                    UpdateGridLayout(e.ViewportWidth);
                }

                if (_currentMode == ViewMode.Grid && _loadedCount < _filteredFiles.Count)
                {
                    if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 200)
                    {
                        LoadNextPage();
                    }
                }
            };
            _scrollViewer.SizeChanged += (s, e) =>
            {
                double w = _scrollViewer.ViewportWidth > 0 ? _scrollViewer.ViewportWidth : e.NewSize.Width;
                UpdateGridLayout(w);
            };

            Grid.SetRow(_scrollViewer, 1);
            _gridView.Children.Add(_scrollViewer);

            Children.Add(_gridView);

            // ================= Detail View (Full Vector CAD View) =================
            _detailView = new Grid();
            _detailView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 0: Top Bar
            _detailView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Row 1: CAD View (WindowsFormsHost)
            _detailView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 2: Bottom Toolbar

            // Row 0: Top Bar (Back button, File name, Star button)
            _detailTopBarBorder = new Border
            {
                Padding = new Thickness(14, 8, 14, 8),
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = DarkCardBorder,
                Background = DarkWindowBg
            };

            var detailTopBar = new Grid();
            detailTopBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            detailTopBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            detailTopBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _btnBackToGrid = new Button
            {
                Content = "◀ 全部图纸",
                ToolTip = "返回缩略图网格 (鼠标侧键后退 / Backspace / Alt+←)",
                Background = DarkToolbarBg,
                Foreground = TextPrimaryDark,
                BorderBrush = DarkCardBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 4, 10, 4),
                Cursor = Cursors.Hand,
                FontSize = 12,
                Focusable = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            _btnBackToGrid.Click += (s, e) => SwitchToGridView();
            Grid.SetColumn(_btnBackToGrid, 0);
            detailTopBar.Children.Add(_btnBackToGrid);

            _txtDetailFileName = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimaryDark,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_txtDetailFileName, 1);
            detailTopBar.Children.Add(_txtDetailFileName);

            _btnFavDetail = CreateToolbarButton("☆ 收藏", "收藏/取消收藏该图纸 (S)");
            _btnFavDetail.Click += (s, e) => ToggleCurrentFavorite();
            Grid.SetColumn(_btnFavDetail, 2);
            detailTopBar.Children.Add(_btnFavDetail);

            _detailTopBarBorder.Child = detailTopBar;
            Grid.SetRow(_detailTopBarBorder, 0);
            _detailView.Children.Add(_detailTopBarBorder);

            // Row 1: Native Vector CAD View via CADPictureBox (No Airspace conflict!)
            _cadPictBox = new CADPictureBox();
            _cadImaging = new CADImaging(_cadPictBox);
            _cadPictBox.Dock = System.Windows.Forms.DockStyle.Fill;
            _cadPictBox.MouseDoubleClick += (s, e) =>
            {
                _cadImaging.ResetScaling();
            };

            _cadImaging.StatusUpdated += (s, e) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_btnZoomReset != null)
                        _btnZoomReset.Content = $"{_cadImaging.RealScale}%";
                }));
            };

            _cadHost = new WindowsFormsHost
            {
                Child = _cadPictBox
            };
            Grid.SetRow(_cadHost, 1);
            _detailView.Children.Add(_cadHost);

            // Row 2: Bottom Toolbar
            _detailBottomBorder = new Border
            {
                Padding = new Thickness(14, 8, 14, 8),
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = DarkCardBorder,
                Background = DarkWindowBg
            };

            var detailPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            _btnPrev = CreateToolbarButton("◀", "上一张 (A / Left / PageUp)");
            _btnPrev.Click += (s, e) => PrevSlide();
            detailPanel.Children.Add(_btnPrev);

            _txtDetailCounter = new TextBlock
            {
                Foreground = TextPrimaryDark,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0)
            };
            detailPanel.Children.Add(_txtDetailCounter);

            _cmbSlides = new ComboBox
            {
                MaxDropDownHeight = 320,
                Width = 160,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Focusable = false
            };
            _cmbSlides.SelectionChanged += (s, e) =>
            {
                if (_cmbSlides.SelectedIndex >= 0 && _cmbSlides.SelectedIndex != _currentIndex)
                {
                    _currentIndex = _cmbSlides.SelectedIndex;
                    RenderCurrentDetailSlide();
                }
            };
            detailPanel.Children.Add(_cmbSlides);

            _btnNext = CreateToolbarButton("▶", "下一张 (D / Right / Space)");
            _btnNext.Click += (s, e) => NextSlide();
            _btnNext.Margin = new Thickness(0, 0, 8, 0);
            detailPanel.Children.Add(_btnNext);

            // 核心功能：直接插入到 CAD 按钮
            _btnInsertCad = new Button
            {
                Content = "📥 插入到CAD",
                ToolTip = "点击此按钮或按 I 键，直接将图纸作为块插入至运行中的 AutoCAD / 浩辰 / 中望CAD",
                Background = AccentBlueBrush,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 4, 12, 4),
                Cursor = Cursors.Hand,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(8, 0, 8, 0),
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Center
            };
            _btnInsertCad.Click += (s, e) => TriggerInsertCurrentToCad();
            detailPanel.Children.Add(_btnInsertCad);

            _btnZoomOut = CreateToolbarButton("➖", "缩小 (-)");
            _btnZoomOut.Click += (s, e) => _cadImaging.ZoomOut();
            detailPanel.Children.Add(_btnZoomOut);

            _btnZoomReset = new Button
            {
                Content = "100%",
                ToolTip = "自适应居中 (双击画布也可还原)",
                Background = Brushes.Transparent,
                Foreground = TextPrimaryDark,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 2, 6, 2),
                Cursor = Cursors.Hand,
                FontSize = 11,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Consolas"),
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Center
            };
            _btnZoomReset.Click += (s, e) => _cadImaging.ResetScaling();
            detailPanel.Children.Add(_btnZoomReset);

            _btnZoomIn = CreateToolbarButton("➕", "放大 (+)");
            _btnZoomIn.Click += (s, e) => _cadImaging.ZoomIn();
            detailPanel.Children.Add(_btnZoomIn);

            _btnDetailTheme = CreateToolbarButton("🌓", "切换底色 (B)");
            _btnDetailTheme.Click += (s, e) => ToggleTheme();
            detailPanel.Children.Add(_btnDetailTheme);

            _detailBottomBorder.Child = detailPanel;
            Grid.SetRow(_detailBottomBorder, 2);
            _detailView.Children.Add(_detailBottomBorder);

            Children.Add(_detailView);

            // ================= Toast Notification Overlay =================
            _toastBorder = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(235, 30, 30, 32)),
                BorderBrush = AccentBlueBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(16, 8, 16, 8),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 50, 0, 0),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };

            _txtToast = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _toastBorder.Child = _txtToast;
            Children.Add(_toastBorder);
        }

        private Button CreateToolbarButton(string text, string tooltip)
        {
            return new Button
            {
                Content = text,
                ToolTip = tooltip,
                Background = Brushes.Transparent,
                Foreground = TextPrimaryDark,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 2, 6, 2),
                Cursor = Cursors.Hand,
                FontSize = 13,
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private Button CreateFilterTabButton(string text, FilterTab tab)
        {
            var btn = new Button
            {
                Content = text,
                Background = Brushes.Transparent,
                Foreground = TextSecondaryDark,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 3, 8, 3),
                Cursor = Cursors.Hand,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(0, 0, 4, 0),
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Center
            };

            btn.Click += (s, e) =>
            {
                _currentTab = tab;
                UpdateTabButtonStyles();
                FilterDrawings();
            };

            return btn;
        }

        private void UpdateTabButtonStyles()
        {
            UpdateSingleTabStyle(_btnTabAll, _currentTab == FilterTab.All);
            UpdateSingleTabStyle(_btnTabFav, _currentTab == FilterTab.Favorites);
            UpdateSingleTabStyle(_btnTabFreq, _currentTab == FilterTab.Frequent);
        }

        private void UpdateSingleTabStyle(Button btn, bool isActive)
        {
            if (isActive)
            {
                btn.Foreground = AccentBlueBrush;
                btn.FontWeight = FontWeights.Bold;
            }
            else
            {
                btn.Foreground = _isDarkBackground ? TextSecondaryDark : TextSecondaryLight;
                btn.FontWeight = FontWeights.Normal;
            }
        }

        private void RefreshCadStatus(bool showToast = false)
        {
            bool running = CadAutomationHelper.CheckRunningCad(out string cadName);
            if (running)
            {
                _txtCadStatus.Text = $"🟢 {cadName}";
                _txtCadStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 200, 100));
                if (showToast) ShowToast($"已连接到正在运行的 {cadName}！双击图纸可直接插入");
            }
            else
            {
                _txtCadStatus.Text = "⚪ CAD未启动";
                _txtCadStatus.Foreground = _isDarkBackground ? TextSecondaryDark : TextSecondaryLight;
                if (showToast) ShowToast("未检测到运行中的 CAD（AutoCAD、浩辰CAD、中望CAD）");
            }
        }

        public void ShowToast(string message)
        {
            _txtToast.Text = message;
            _toastBorder.Visibility = Visibility.Visible;

            _toastTimer?.Stop();
            _toastTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _toastTimer.Tick += (s, e) =>
            {
                _toastBorder.Visibility = Visibility.Collapsed;
                _toastTimer.Stop();
            };
            _toastTimer.Start();
        }

        private void UpdateGridLayout(double viewportWidth)
        {
            if (viewportWidth <= 100) return;

            double contentWidth = viewportWidth - 28;
            if (contentWidth <= 100) return;

            int cols = ConfigManager.Columns;
            if (cols <= 0)
            {
                cols = (int)Math.Max(2, Math.Round(contentWidth / 160.0));
            }

            _uniformGrid.Columns = cols;

            double cellWidth = contentWidth / cols;
            double cardHeight = Math.Max(100, Math.Round(cellWidth * 0.78));

            foreach (UIElement child in _uniformGrid.Children)
            {
                if (child is Border card)
                {
                    card.Height = cardHeight;
                }
            }
        }

        private void ApplyTheme()
        {
            Background = _isDarkBackground ? DarkWindowBg : LightWindowBg;

            Brush primaryText = _isDarkBackground ? TextPrimaryDark : TextPrimaryLight;
            Brush secondaryText = _isDarkBackground ? TextSecondaryDark : TextSecondaryLight;
            Brush toolbarBg = _isDarkBackground ? DarkToolbarBg : LightToolbarBg;
            Brush cardBorder = _isDarkBackground ? DarkCardBorder : LightCardBorder;

            _txtGridTitle.Foreground = primaryText;
            _txtGridCount.Foreground = secondaryText;
            _btnGridTheme.Foreground = primaryText;
            _btnGridTheme.Content = _isDarkBackground ? "🌓" : "☀️";
            _btnDetailTheme.Content = _isDarkBackground ? "🌓" : "☀️";

            _searchBorder.Background = _isDarkBackground ? DarkSearchBg : LightSearchBg;
            _searchBorder.BorderBrush = cardBorder;
            _searchIcon.Stroke = secondaryText;
            _txtPlaceholder.Foreground = secondaryText;
            _txtSearch.Foreground = primaryText;
            _txtSearch.CaretBrush = primaryText;
            _btnClearSearch.Foreground = secondaryText;

            _detailTopBarBorder.Background = _isDarkBackground ? DarkWindowBg : LightWindowBg;
            _detailTopBarBorder.BorderBrush = cardBorder;
            _detailBottomBorder.Background = _isDarkBackground ? DarkWindowBg : LightWindowBg;
            _detailBottomBorder.BorderBrush = cardBorder;

            _btnBackToGrid.Background = toolbarBg;
            _btnBackToGrid.Foreground = primaryText;
            _btnBackToGrid.BorderBrush = cardBorder;
            _txtDetailFileName.Foreground = primaryText;
            _txtDetailCounter.Foreground = primaryText;
            _btnPrev.Foreground = primaryText;
            _btnNext.Foreground = primaryText;
            _btnDetailTheme.Foreground = primaryText;

            if (_btnZoomOut != null) _btnZoomOut.Foreground = primaryText;
            if (_btnZoomReset != null) _btnZoomReset.Foreground = primaryText;
            if (_btnZoomIn != null) _btnZoomIn.Foreground = primaryText;

            UpdateTabButtonStyles();

            // Update cards
            foreach (UIElement child in _uniformGrid.Children)
            {
                if (child is Border card)
                {
                    card.Background = _isDarkBackground ? DarkCardBg : LightCardBg;
                    card.BorderBrush = cardBorder;
                    if (card.Child is Grid cardGrid && cardGrid.Children.Count >= 2 && cardGrid.Children[cardGrid.Children.Count - 1] is TextBlock tb)
                    {
                        tb.Foreground = primaryText;
                    }
                }
            }
        }

        private void UpdateViewVisibility()
        {
            if (_currentMode == ViewMode.Grid)
            {
                _gridView.Visibility = Visibility.Visible;
                _detailView.Visibility = Visibility.Collapsed;
                _context.Title = $"{Path.GetFileName(_folderPath)} (共 {_allDrawingFiles.Count} 张 CAD 图纸)";
            }
            else
            {
                _gridView.Visibility = Visibility.Collapsed;
                _detailView.Visibility = Visibility.Visible;
            }
        }

        public void LoadContent()
        {
            try
            {
                RefreshCadStatus();

                if (!Directory.Exists(_folderPath))
                {
                    _allDrawingFiles = new List<string> { _initialFilePath };
                }
                else
                {
                    var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".dwg", ".dxf" };
                    _allDrawingFiles = Directory.EnumerateFiles(_folderPath, "*.*")
                        .Where(f => exts.Contains(Path.GetExtension(f)))
                        .ToList();
                }

                _context.Title = $"{Path.GetFileName(_folderPath)} (共 {_allDrawingFiles.Count} 张 CAD 图纸)";

                SortDrawingFiles();

                _cmbSlides.Items.Clear();
                foreach (var f in _allDrawingFiles)
                {
                    _cmbSlides.Items.Add(Path.GetFileName(f));
                }

                _currentIndex = _allDrawingFiles.FindIndex(f => string.Equals(f, _initialFilePath, StringComparison.OrdinalIgnoreCase));
                if (_currentIndex < 0) _currentIndex = 0;

                _filteredFiles = new List<string>(_allDrawingFiles);
                ResetAndLoadFirstPage();
            }
            catch (Exception ex)
            {
                ShowError($"加载图纸失败: {ex.Message}");
            }
            finally
            {
                _context.IsBusy = false;
            }
        }

        private void SortDrawingFiles()
        {
            _allDrawingFiles.Sort((a, b) =>
            {
                bool favA = ConfigManager.IsFavorite(a);
                bool favB = ConfigManager.IsFavorite(b);
                if (favA != favB) return favB.CompareTo(favA);

                int countA = ConfigManager.GetInsertCount(a);
                int countB = ConfigManager.GetInsertCount(b);
                if (countA != countB) return countB.CompareTo(countA);

                return string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase);
            });
        }

        private void ResetAndLoadFirstPage()
        {
            _thumbnailCts?.Cancel();
            _thumbnailCts = new CancellationTokenSource();
            lock (_queueLock)
            {
                _thumbnailQueue.Clear();
                _isThumbnailWorkerRunning = false;
            }

            _uniformGrid.Children.Clear();
            _loadedCount = 0;

            LoadNextPage();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_currentMode == ViewMode.Grid && _loadedCount < _filteredFiles.Count)
                {
                    if (_scrollViewer.ExtentHeight <= _scrollViewer.ViewportHeight + 20)
                    {
                        LoadNextPage();
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private bool LoadNextPage()
        {
            if (_loadedCount >= _filteredFiles.Count)
            {
                UpdateGridCount();
                return false;
            }

            int countToLoad = Math.Min(PageSize, _filteredFiles.Count - _loadedCount);
            var newBatch = new List<string>(countToLoad);

            double currentWidth = _scrollViewer.ViewportWidth > 0 ? _scrollViewer.ViewportWidth : _scrollViewer.ActualWidth;
            double contentWidth = currentWidth > 100 ? currentWidth - 28 : 800;

            int cols = ConfigManager.Columns;
            if (cols <= 0) cols = (int)Math.Max(2, Math.Round(contentWidth / 160.0));
            double cellWidth = contentWidth / cols;
            double cardHeight = Math.Max(100, Math.Round(cellWidth * 0.78));

            for (int i = 0; i < countToLoad; i++)
            {
                string filePath = _filteredFiles[_loadedCount + i];
                newBatch.Add(filePath);
                int originalIndex = _allDrawingFiles.IndexOf(filePath);

                var card = CreateDrawingCard(filePath, originalIndex);
                card.Height = cardHeight;
                _uniformGrid.Children.Add(card);
            }

            _loadedCount += countToLoad;

            UpdateGridLayout(currentWidth);
            UpdateGridCount();

            EnqueueBatchThumbnailLoading(newBatch);

            return true;
        }

        private void UpdateGridCount()
        {
            if (_filteredFiles.Count == 0)
            {
                _txtGridCount.Text = "(0 张)";
            }
            else if (_loadedCount < _filteredFiles.Count)
            {
                _txtGridCount.Text = $"(已载入 {_loadedCount} / 共 {_filteredFiles.Count} 张)";
            }
            else
            {
                _txtGridCount.Text = $"(共 {_filteredFiles.Count} 张)";
            }
        }

        private Border CreateDrawingCard(string filePath, int fileIndex)
        {
            string fileName = Path.GetFileName(filePath);
            bool isFav = ConfigManager.IsFavorite(filePath);
            int insertCount = ConfigManager.GetInsertCount(filePath);

            var card = new Border
            {
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(7),
                Background = _isDarkBackground ? DarkCardBg : LightCardBg,
                BorderBrush = _isDarkBackground ? DarkCardBorder : LightCardBorder,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ClipToBounds = true,
                Tag = fileIndex
            };

            var cardGrid = new Grid();
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var img = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 6, 6, 2)
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            if (_thumbnailCache.TryGetValue(filePath, out var cachedSource))
            {
                img.Source = cachedSource;
            }

            Grid.SetRow(img, 0);
            cardGrid.Children.Add(img);

            // Top-Right Favorite Star Button
            var btnFav = new Button
            {
                Content = isFav ? "⭐" : "☆",
                Foreground = isFav ? GoldStarBrush : TextSecondaryDark,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize = 13,
                Padding = new Thickness(4, 2, 4, 2),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Focusable = false,
                ToolTip = isFav ? "已收藏 (点击取消)" : "点击收藏"
            };
            btnFav.Click += (s, e) =>
            {
                e.Handled = true;
                bool newStatus = ConfigManager.ToggleFavorite(filePath);
                btnFav.Content = newStatus ? "⭐" : "☆";
                btnFav.Foreground = newStatus ? GoldStarBrush : TextSecondaryDark;
                btnFav.ToolTip = newStatus ? "已收藏 (点击取消)" : "点击收藏";
                ShowToast(newStatus ? $"已收藏: {fileName}" : $"已取消收藏: {fileName}");
            };
            Grid.SetRow(btnFav, 0);
            cardGrid.Children.Add(btnFav);

            // Top-Left Insert Count Badge
            if (insertCount > 0)
            {
                var badge = new Border
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(190, 230, 80, 0)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(4, 1, 4, 1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(4, 4, 0, 0),
                    ToolTip = $"已插入 {insertCount} 次"
                };
                var txtBadge = new TextBlock
                {
                    Text = $"🔥 {insertCount}",
                    FontSize = 9,
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold
                };
                badge.Child = txtBadge;
                Grid.SetRow(badge, 0);
                cardGrid.Children.Add(badge);
            }

            // Bottom Name Caption
            var tb = new TextBlock
            {
                Text = fileName,
                FontSize = 11,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI"),
                Foreground = _isDarkBackground ? TextPrimaryDark : TextPrimaryLight,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 2, 6, 6),
                ToolTip = $"{fileName}\n完整路径: {filePath}\n双击直接插入运行中的 CAD"
            };
            Grid.SetRow(tb, 1);
            cardGrid.Children.Add(tb);

            card.Child = cardGrid;

            card.MouseEnter += (s, e) =>
            {
                card.BorderBrush = AccentBlueBrush;
                card.BorderThickness = new Thickness(1.5);
                card.Background = _isDarkBackground ? DarkCardHoverBg : LightCardHoverBg;
            };
            card.MouseLeave += (s, e) =>
            {
                card.BorderBrush = _isDarkBackground ? DarkCardBorder : LightCardBorder;
                card.BorderThickness = new Thickness(1);
                card.Background = _isDarkBackground ? DarkCardBg : LightCardBg;
            };

            card.MouseLeftButtonUp += (s, e) =>
            {
                if (e.ClickCount >= 2)
                {
                    if (CadAutomationHelper.CheckRunningCad(out _))
                    {
                        TriggerInsertCad(filePath);
                    }
                    else
                    {
                        SwitchToDetailView(fileIndex);
                    }
                }
                else
                {
                    SwitchToDetailView(fileIndex);
                }
            };

            return card;
        }

        private void FilterDrawings()
        {
            string keyword = _txtSearch.Text.Trim();
            IEnumerable<string> query = _allDrawingFiles;

            if (_currentTab == FilterTab.Favorites)
            {
                query = query.Where(ConfigManager.IsFavorite);
            }
            else if (_currentTab == FilterTab.Frequent)
            {
                query = query.Where(f => ConfigManager.GetInsertCount(f) > 0);
            }

            if (!string.IsNullOrEmpty(keyword))
            {
                query = query.Where(f => Path.GetFileName(f).IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            _filteredFiles = query.ToList();
            ResetAndLoadFirstPage();
        }

        private void EnqueueBatchThumbnailLoading(List<string> files)
        {
            lock (_queueLock)
            {
                foreach (var f in files)
                {
                    if (!_thumbnailCache.ContainsKey(f) && !_thumbnailQueue.Contains(f))
                    {
                        _thumbnailQueue.Enqueue(f);
                    }
                }

                if (!_isThumbnailWorkerRunning && _thumbnailQueue.Count > 0)
                {
                    _isThumbnailWorkerRunning = true;
                    StartThumbnailWorker();
                }
            }
        }

        private void StartThumbnailWorker()
        {
            var cts = _thumbnailCts;
            if (cts == null) return;
            var token = cts.Token;

            DpiScale dpi = GetCurrentDpi();
            float dpiFactor = (float)dpi.DpiScaleX;
            int thumbSize = (int)Math.Round(240 * dpiFactor);

            Task.Run(() =>
            {
                while (true)
                {
                    string? filePath = null;

                    lock (_queueLock)
                    {
                        if (token.IsCancellationRequested || _thumbnailQueue.Count == 0)
                        {
                            _isThumbnailWorkerRunning = false;
                            break;
                        }
                        filePath = _thumbnailQueue.Dequeue();
                    }

                    if (filePath == null) break;
                    if (_thumbnailCache.ContainsKey(filePath)) continue;

                    bool isDark = _isDarkBackground;
                    try
                    {
                        using (var bmp = DwgThumbnailExtractor.RenderCadDrawing(filePath, thumbSize, (int)(thumbSize * 0.75), isDark))
                        {
                            if (bmp != null)
                            {
                                using (var ms = new MemoryStream())
                                {
                                    bmp.Save(ms, ImageFormat.Png);
                                    ms.Position = 0;
                                    var bi = new BitmapImage();
                                    bi.BeginInit();
                                    bi.StreamSource = ms;
                                    bi.CacheOption = BitmapCacheOption.OnLoad;
                                    bi.EndInit();
                                    bi.Freeze();

                                    _thumbnailCache[filePath] = bi;

                                    Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        UpdateCardImage(filePath, bi);
                                    }));
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }, token);
        }

        private void UpdateCardImage(string filePath, ImageSource imageSource)
        {
            string fileName = Path.GetFileName(filePath);
            foreach (UIElement child in _uniformGrid.Children)
            {
                if (child is Border card && card.Child is Grid cardGrid && cardGrid.Children.Count >= 2)
                {
                    if (cardGrid.Children[cardGrid.Children.Count - 1] is TextBlock tb &&
                        string.Equals(tb.Text, fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (cardGrid.Children[0] is Image img)
                        {
                            img.Source = imageSource;
                        }
                        break;
                    }
                }
            }
        }

        private void SwitchToDetailView(int fileIndex)
        {
            if (_txtSearch.IsFocused)
            {
                Keyboard.ClearFocus();
            }

            _currentIndex = Math.Max(0, Math.Min(fileIndex, _allDrawingFiles.Count - 1));
            _currentMode = ViewMode.Detail;
            UpdateViewVisibility();
            RenderCurrentDetailSlide();

            Focus();
            Keyboard.Focus(this);
        }

        private void SwitchToGridView()
        {
            while (_loadedCount <= _currentIndex && _loadedCount < _filteredFiles.Count)
            {
                LoadNextPage();
            }

            _currentMode = ViewMode.Grid;
            UpdateViewVisibility();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                string currentFile = _allDrawingFiles[_currentIndex];
                string currentName = Path.GetFileName(currentFile);
                foreach (UIElement child in _uniformGrid.Children)
                {
                    if (child is Border card && card.Child is Grid cardGrid &&
                        cardGrid.Children[cardGrid.Children.Count - 1] is TextBlock tb &&
                        string.Equals(tb.Text, currentName, StringComparison.OrdinalIgnoreCase))
                    {
                        card.BringIntoView();
                        break;
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Background);

            Focus();
        }

        private void RenderCurrentDetailSlide()
        {
            try
            {
                if (_currentIndex < 0 || _currentIndex >= _allDrawingFiles.Count)
                    return;

                string filePath = _allDrawingFiles[_currentIndex];
                string fileName = Path.GetFileName(filePath);

                _txtDetailCounter.Text = $"{_currentIndex + 1} / {_allDrawingFiles.Count}";
                _cmbSlides.SelectedIndex = _currentIndex;
                _btnPrev.IsEnabled = _currentIndex > 0;
                _btnNext.IsEnabled = _currentIndex < _allDrawingFiles.Count - 1;

                bool isFav = ConfigManager.IsFavorite(filePath);
                _btnFavDetail.Content = isFav ? "⭐ 已收藏" : "☆ 收藏";
                _btnFavDetail.Foreground = isFav ? GoldStarBrush : TextPrimaryDark;

                _context.Title = $"[{_currentIndex + 1}/{_allDrawingFiles.Count}] {fileName} - {Path.GetFileName(_folderPath)}";
                _txtDetailFileName.Text = $"{fileName}  ({_currentIndex + 1} / {_allDrawingFiles.Count})";

                // 由 CADImaging 驱动 CADPictureBox 进行纯原生 GDI+ 矢量硬件双缓冲渲染！
                // 彻底解决放大模糊问题，任意放大比例（100%、306%、1000%）文字与线条百分之百矢量锐利！
                _cadImaging.SetBackColor(_isDarkBackground);
                _cadImaging.LoadFile(filePath);
            }
            catch (Exception ex)
            {
                ShowToast($"预览出错: {ex.Message}");
            }
        }

        private void TriggerInsertCurrentToCad()
        {
            if (_currentIndex >= 0 && _currentIndex < _allDrawingFiles.Count)
            {
                TriggerInsertCad(_allDrawingFiles[_currentIndex]);
            }
        }

        private void TriggerInsertCad(string filePath)
        {
            bool success = CadAutomationHelper.InsertDrawingIntoCad(filePath, out string msg);
            if (success)
            {
                ConfigManager.RecordInsert(filePath);
                ShowToast(msg);
                RefreshCadStatus();
            }
            else
            {
                ShowToast(msg);
            }
        }

        private void ToggleCurrentFavorite()
        {
            if (_currentIndex >= 0 && _currentIndex < _allDrawingFiles.Count)
            {
                string filePath = _allDrawingFiles[_currentIndex];
                bool newStatus = ConfigManager.ToggleFavorite(filePath);
                _btnFavDetail.Content = newStatus ? "⭐ 已收藏" : "☆ 收藏";
                _btnFavDetail.Foreground = newStatus ? GoldStarBrush : TextPrimaryDark;
                ShowToast(newStatus ? "已加入收藏" : "已取消收藏");
            }
        }

        private void PrevSlide()
        {
            if (_currentIndex > 0)
            {
                _currentIndex--;
                RenderCurrentDetailSlide();
            }
        }

        private void NextSlide()
        {
            if (_currentIndex < _allDrawingFiles.Count - 1)
            {
                _currentIndex++;
                RenderCurrentDetailSlide();
            }
        }

        private void ToggleTheme()
        {
            _isDarkBackground = !_isDarkBackground;
            ConfigManager.DarkTheme = _isDarkBackground;
            ApplyTheme();

            if (_currentMode == ViewMode.Detail)
            {
                _cadImaging.SetBackColor(_isDarkBackground);
            }
        }

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            Focus();

            if (e.ChangedButton == MouseButton.XButton1)
            {
                if (_currentMode == ViewMode.Detail)
                {
                    SwitchToGridView();
                    e.Handled = true;
                    return;
                }
            }
            else if (e.ChangedButton == MouseButton.XButton2)
            {
                if (_currentMode == ViewMode.Grid)
                {
                    SwitchToDetailView(_currentIndex);
                    e.Handled = true;
                    return;
                }
                else if (_currentMode == ViewMode.Detail)
                {
                    NextSlide();
                    e.Handled = true;
                    return;
                }
            }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key;
            if (key == Key.ImeProcessed) key = e.ImeProcessedKey;
            if (key == Key.System) key = e.SystemKey;

            if (key == Key.B && !_txtSearch.IsFocused)
            {
                ToggleTheme();
                e.Handled = true;
                return;
            }

            if (_currentMode == ViewMode.Detail)
            {
                if (key == Key.Back || (key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt))
                {
                    SwitchToGridView();
                    e.Handled = true;
                    return;
                }

                switch (key)
                {
                    case Key.A:
                    case Key.Left:
                    case Key.PageUp:
                        PrevSlide();
                        e.Handled = true;
                        break;

                    case Key.D:
                    case Key.Right:
                    case Key.PageDown:
                    case Key.Space:
                        NextSlide();
                        e.Handled = true;
                        break;

                    case Key.I:
                        TriggerInsertCurrentToCad();
                        e.Handled = true;
                        break;

                    case Key.S:
                        ToggleCurrentFavorite();
                        e.Handled = true;
                        break;

                    case Key.OemPlus:
                    case Key.Add:
                        _cadImaging.ZoomIn();
                        e.Handled = true;
                        break;

                    case Key.OemMinus:
                    case Key.Subtract:
                        _cadImaging.ZoomOut();
                        e.Handled = true;
                        break;

                    case Key.D0:
                    case Key.NumPad0:
                    case Key.F:
                        _cadImaging.ResetScaling();
                        e.Handled = true;
                        break;

                    case Key.Home:
                        if (_currentIndex != 0)
                        {
                            _currentIndex = 0;
                            RenderCurrentDetailSlide();
                        }
                        e.Handled = true;
                        break;

                    case Key.End:
                        if (_currentIndex != _allDrawingFiles.Count - 1)
                        {
                            _currentIndex = _allDrawingFiles.Count - 1;
                            RenderCurrentDetailSlide();
                        }
                        e.Handled = true;
                        break;
                }
            }
            else if (_currentMode == ViewMode.Grid)
            {
                if (key == Key.Enter && !_txtSearch.IsFocused)
                {
                    if (_filteredFiles.Count > 0)
                    {
                        int index = _allDrawingFiles.IndexOf(_filteredFiles[0]);
                        if (index >= 0)
                        {
                            SwitchToDetailView(index);
                            e.Handled = true;
                        }
                    }
                }
            }
        }

        private void ShowError(string message)
        {
            ShowToast(message);
            _currentMode = ViewMode.Grid;
            UpdateViewVisibility();
        }

        private DpiScale GetCurrentDpi()
        {
            try
            {
                return VisualTreeHelper.GetDpi(this);
            }
            catch
            {
                return new DpiScale(1.0, 1.0);
            }
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);

            if (_currentMode == ViewMode.Detail)
            {
                _cadImaging.Resize();
            }
            else if (_currentMode == ViewMode.Grid)
            {
                double currentWidth = _scrollViewer.ViewportWidth > 0 ? _scrollViewer.ViewportWidth : ActualWidth;
                UpdateGridLayout(currentWidth);
            }
        }

        private static ImageSource BitmapToImageSource(Bitmap bitmap)
        {
            IntPtr hBitmap = bitmap.GetHbitmap();
            try
            {
                return Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }

        public void Dispose()
        {
            _toastTimer?.Stop();
            _toastTimer = null;

            _thumbnailCts?.Cancel();
            _thumbnailCts = null;
            lock (_queueLock)
            {
                _thumbnailQueue.Clear();
                _isThumbnailWorkerRunning = false;
            }
            _thumbnailCache.Clear();

            _cadImaging?.Dispose();
            _cadPictBox?.Dispose();
            _cadHost?.Dispose();
        }
    }
}
