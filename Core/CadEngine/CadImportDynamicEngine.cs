using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;

namespace QuickLook.Plugin.DwgsViewer.Core.CadEngine
{
    /// <summary>
    /// CADSoftTools (CADImport.dll) 动态反射驱动引擎。
    /// 【完全解耦设计】：无任何编译期硬引用，仅在用户自行放入 CADImport.dll 时动态加载。
    /// 包含内置线程安全锁（避免多线程 SGLines 内存死循环）及授权试用状态反射修补。
    /// </summary>
    public class CadImportDynamicEngine : ICadEngine
    {
        public string Name => "CADSoftTools Vector Engine (Dynamic Driver)";
        public bool IsAvailable { get; private set; }

        private static readonly object _cadImportLock = new object();

        private Assembly? _asm;
        private Type? _cadImageType;
        private MethodInfo? _createImageMethod;
        private MethodInfo? _loadFromFileMethod;
        private MethodInfo? _drawMethod;
        private PropertyInfo? _absWidthProp;
        private PropertyInfo? _absHeightProp;
        private PropertyInfo? _bgColorProp;
        private PropertyInfo? _defaultColorProp;
        private PropertyInfo? _lineWeightProp;
        private PropertyInfo? _objEntityCadImageProp;

        private object? _currentCadImage;
        private bool _isDark = true;
        private double _imgW, _imgH;

        public CadImportDynamicEngine()
        {
            TryInitialize();
        }

        private void TryInitialize()
        {
            try
            {
                string? dllDir = FindDriverDirectory();
                if (dllDir == null)
                {
                    IsAvailable = false;
                    return;
                }

                string dllPath = Path.Combine(dllDir, "CADImport.dll");
                if (!File.Exists(dllPath))
                {
                    IsAvailable = false;
                    return;
                }

                _asm = Assembly.LoadFrom(dllPath);

                // 反射修补试用标志位，防止放大时出现 Trial Version 水印
                try
                {
                    var field = _asm.ManifestModule.ResolveField(0x040005B7);
                    field?.SetValue(null, 1);
                }
                catch
                {
                }

                _cadImageType = _asm.GetType("CADImport.CADImage");
                if (_cadImageType != null)
                {
                    _createImageMethod = _cadImageType.GetMethod("CreateImageByExtension", new[] { typeof(string) });
                    _loadFromFileMethod = _cadImageType.GetMethod("LoadFromFile", new[] { typeof(string) });
                    _drawMethod = _cadImageType.GetMethod("Draw", new[] { typeof(Graphics), typeof(RectangleF) });
                    _absWidthProp = _cadImageType.GetProperty("AbsWidth");
                    _absHeightProp = _cadImageType.GetProperty("AbsHeight");
                    _bgColorProp = _cadImageType.GetProperty("BackgroundColor");
                    _defaultColorProp = _cadImageType.GetProperty("DefaultColor");
                    _lineWeightProp = _cadImageType.GetProperty("IsShowLineWeight");
                }

                var objEntityType = _asm.GetType("CADImport.ObjEntity");
                _objEntityCadImageProp = objEntityType?.GetProperty("cadImage");

                IsAvailable = _createImageMethod != null && _loadFromFileMethod != null && _drawMethod != null;
            }
            catch
            {
                IsAvailable = false;
            }
        }

        private static string? FindDriverDirectory()
        {
            string[] probePaths = {
                AppDomain.CurrentDomain.BaseDirectory,
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Drivers"),
                Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "Drivers"),
                @"C:\Users\Hexon\AppData\Roaming\pooi.moe\QuickLook\QuickLook.Plugin\QuickLook.Plugin.DwgsViewer.bak"
            };

            foreach (var dir in probePaths)
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    if (File.Exists(Path.Combine(dir, "CADImport.dll")))
                        return dir;
                }
            }
            return null;
        }

        public Bitmap? RenderThumbnail(string filePath, int width, int height, bool isDark)
        {
            if (!IsAvailable || string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            // CADImport 内部使用静态 ObjEntity.cadImage，必须串行加锁保证线程安全
            lock (_cadImportLock)
            {
                object? cadImage = null;
                try
                {
                    cadImage = _createImageMethod?.Invoke(null, new object[] { filePath });
                    if (cadImage == null) return null;

                    _loadFromFileMethod?.Invoke(cadImage, new object[] { filePath });
                    _objEntityCadImageProp?.SetValue(null, cadImage, null);

                    _lineWeightProp?.SetValue(cadImage, false, null);
                    _bgColorProp?.SetValue(cadImage, isDark ? Color.Black : Color.White, null);
                    _defaultColorProp?.SetValue(cadImage, isDark ? Color.White : Color.Black, null);

                    double imgW = (double)(_absWidthProp?.GetValue(cadImage, null) ?? 100.0);
                    double imgH = (double)(_absHeightProp?.GetValue(cadImage, null) ?? 100.0);
                    if (imgW <= 0) imgW = 100;
                    if (imgH <= 0) imgH = 100;

                    var bmp = new Bitmap(width, height);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(isDark ? Color.FromArgb(24, 24, 26) : Color.White);
                        g.SmoothingMode = SmoothingMode.AntiAlias;

                        float wh = (float)(imgW / imgH);
                        float targetWH = (float)width / height;
                        SizeF visibleArea;
                        if (targetWH > wh)
                            visibleArea = new SizeF(height * wh, height);
                        else
                            visibleArea = new SizeF(width, width / wh);

                        float left = (width - visibleArea.Width) / 2f;
                        float top = (height - visibleArea.Height) / 2f;
                        var rect = new RectangleF(left, top, visibleArea.Width, visibleArea.Height);

                        _drawMethod?.Invoke(cadImage, new object[] { g, rect });
                    }
                    return bmp;
                }
                catch
                {
                    return null;
                }
                finally
                {
                    if (cadImage is IDisposable disp)
                        disp.Dispose();
                }
            }
        }

        public bool Load(string filePath, bool isDark)
        {
            if (!IsAvailable || string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return false;

            lock (_cadImportLock)
            {
                Unload();
                _isDark = isDark;

                try
                {
                    _currentCadImage = _createImageMethod?.Invoke(null, new object[] { filePath });
                    if (_currentCadImage == null) return false;

                    _loadFromFileMethod?.Invoke(_currentCadImage, new object[] { filePath });
                    _objEntityCadImageProp?.SetValue(null, _currentCadImage, null);

                    _lineWeightProp?.SetValue(_currentCadImage, false, null);
                    ApplyColors(_currentCadImage, isDark);

                    _imgW = (double)(_absWidthProp?.GetValue(_currentCadImage, null) ?? 100.0);
                    _imgH = (double)(_absHeightProp?.GetValue(_currentCadImage, null) ?? 100.0);
                    if (_imgW <= 0) _imgW = 100;
                    if (_imgH <= 0) _imgH = 100;

                    return true;
                }
                catch
                {
                    Unload();
                    return false;
                }
            }
        }

        public void RenderDetail(Graphics g, Rectangle clientRect, float zoom, float panX, float panY)
        {
            if (!IsAvailable || _currentCadImage == null || clientRect.Width <= 0 || clientRect.Height <= 0)
                return;

            lock (_cadImportLock)
            {
                try
                {
                    _objEntityCadImageProp?.SetValue(null, _currentCadImage, null);

                    g.SmoothingMode = SmoothingMode.AntiAlias;

                    float wh = (float)(_imgW / _imgH);
                    float clientWH = (float)clientRect.Width / clientRect.Height;

                    SizeF baseSize;
                    if (clientWH > wh)
                        baseSize = new SizeF((clientRect.Height - 40) * wh, clientRect.Height - 40);
                    else
                        baseSize = new SizeF(clientRect.Width - 40, (clientRect.Width - 40) / wh);

                    float drawW = baseSize.Width * zoom;
                    float drawH = baseSize.Height * zoom;

                    float drawX = (clientRect.Width - drawW) / 2f + panX;
                    float drawY = (clientRect.Height - drawH) / 2f + panY;

                    var rect = new RectangleF(drawX, drawY, drawW, drawH);
                    _drawMethod?.Invoke(_currentCadImage, new object[] { g, rect });
                }
                catch
                {
                }
            }
        }

        public void SetBackColor(bool isDark)
        {
            _isDark = isDark;
            if (_currentCadImage != null)
            {
                lock (_cadImportLock)
                {
                    ApplyColors(_currentCadImage, isDark);
                }
            }
        }

        private void ApplyColors(object cadImage, bool isDark)
        {
            _bgColorProp?.SetValue(cadImage, isDark ? Color.Black : Color.White, null);
            _defaultColorProp?.SetValue(cadImage, isDark ? Color.White : Color.Black, null);
        }

        public void Unload()
        {
            if (_currentCadImage is IDisposable disp)
            {
                try { disp.Dispose(); } catch { }
            }
            _currentCadImage = null;
        }

        public void Dispose()
        {
            Unload();
        }
    }
}
