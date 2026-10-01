using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;

namespace QuickLook.Plugin.DwgsViewer.Core.CadEngine
{
    /// <summary>
    /// CadLib (WW.Cad) 动态反射驱动引擎。
    /// 【完全解耦设计】：不产生任何编译期硬依赖，源码中零商业二进制引用。
    /// 仅在用户本地目录存在 WW.Cad.dll 时动态加载执行；发布包和 GitHub 仓库 100% 干净合规。
    /// </summary>
    internal class CadLibDynamicEngine : ICadEngine
    {
        public string Name => "CadLib Vector Engine (Dynamic Driver)";
        public bool IsAvailable { get; private set; }

        private Assembly? _asmCad;
        private Assembly? _asmWW;

        // Reflection metadata cache
        private Type? _dwgReaderType;
        private MethodInfo? _dwgReadMethod;
        private Type? _dxfReaderType;
        private MethodInfo? _dxfReadMethod;
        private Type? _graphicsConfigType;
        private PropertyInfo? _cfgBackColorProp;
        private PropertyInfo? _cfgColorCorrectProp;
        private Type? _argbColorType;
        private ConstructorInfo? _argbCtor;
        private Type? _gdiGraphics3DType;
        private MethodInfo? _createDrawablesMethod;
        private MethodInfo? _boundingBoxMethod;
        private MethodInfo? _gdiDrawMethod;
        private Type? _bounds3DType;
        private Type? _trans4DType;
        private MethodInfo? _getBoundsTransformMethod;
        private MethodInfo? _translationMethod;
        private MethodInfo? _scalingMethod;
        private MethodInfo? _matrixMultiplyMethod;
        private Type? _matrix4DType;
        private FieldInfo? _matrixIdentityField;
        private Type? _imageExporterType;
        private MethodInfo? _createAutoSizedBitmapMethod;

        // Active state
        private object? _currentModel;
        private object? _cadGraphics;
        private object? _graphicsConfig;
        private bool _isDark = true;

        public CadLibDynamicEngine()
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

                string cadDll = Path.Combine(dllDir, "WW.Cad.dll");
                string wwDll = Path.Combine(dllDir, "WW.dll");

                if (!File.Exists(cadDll) || !File.Exists(wwDll))
                {
                    IsAvailable = false;
                    return;
                }

                _asmCad = Assembly.LoadFrom(cadDll);
                _asmWW = Assembly.LoadFrom(wwDll);

                // DwgReader / DxfReader
                _dwgReaderType = _asmCad.GetType("WW.Cad.IO.DwgReader");
                _dwgReadMethod = _dwgReaderType?.GetMethod("Read", new[] { typeof(string) });
                _dxfReaderType = _asmCad.GetType("WW.Cad.IO.DxfReader");
                _dxfReadMethod = _dxfReaderType?.GetMethod("Read", new[] { typeof(string) });

                // GraphicsConfig
                _graphicsConfigType = _asmCad.GetType("WW.Cad.Drawing.GraphicsConfig");
                _cfgBackColorProp = _graphicsConfigType?.GetProperty("BackColor");
                _cfgColorCorrectProp = _graphicsConfigType?.GetProperty("CorrectColorForBackgroundColor");

                // ArgbColor
                _argbColorType = _asmWW.GetType("WW.Drawing.ArgbColor");
                _argbCtor = _argbColorType?.GetConstructor(new[] { typeof(int), typeof(int), typeof(int) });

                // GDIGraphics3D
                _gdiGraphics3DType = _asmCad.GetType("WW.Cad.Drawing.GDI.GDIGraphics3D");
                _bounds3DType = _asmWW.GetType("WW.Math.Bounds3D");

                if (_gdiGraphics3DType != null && _bounds3DType != null)
                {
                    var modelBaseType = _asmCad.GetType("WW.Cad.Model.DxfModel");
                    _createDrawablesMethod = _gdiGraphics3DType.GetMethod("CreateDrawables", new[] { modelBaseType });
                    _boundingBoxMethod = _gdiGraphics3DType.GetMethod("BoundingBox", new[] { _bounds3DType });
                }

                // Transformation4D & Matrix4D
                _trans4DType = _asmWW.GetType("WW.Math.Transformation4D");
                _matrix4DType = _asmWW.GetType("WW.Math.Matrix4D");
                _matrixIdentityField = _matrix4DType?.GetField("Identity", BindingFlags.Public | BindingFlags.Static);
                _matrixMultiplyMethod = _matrix4DType?.GetMethod("op_Multiply", new[] { _matrix4DType, _matrix4DType });

                if (_trans4DType != null && _bounds3DType != null)
                {
                    _getBoundsTransformMethod = _trans4DType.GetMethod("GetBoundsToScreenTransform",
                        new[] { _bounds3DType, typeof(int), typeof(int), typeof(int) });
                    _translationMethod = _trans4DType.GetMethod("Translation",
                        new[] { typeof(double), typeof(double), typeof(double) });
                    _scalingMethod = _trans4DType.GetMethod("Scaling",
                        new[] { typeof(double), typeof(double), typeof(double) });
                }

                if (_gdiGraphics3DType != null && _matrix4DType != null)
                {
                    _gdiDrawMethod = _gdiGraphics3DType.GetMethod("Draw",
                        new[] { typeof(Graphics), typeof(Rectangle), _matrix4DType });
                }

                // ImageExporter
                _imageExporterType = _asmCad.GetType("WW.Cad.IO.ImageExporter");
                if (_imageExporterType != null && _matrix4DType != null && _graphicsConfigType != null)
                {
                    var modelBaseType = _asmCad.GetType("WW.Cad.Model.DxfModel");
                    _createAutoSizedBitmapMethod = _imageExporterType.GetMethod("CreateAutoSizedBitmap", new[] {
                        modelBaseType, _matrix4DType, _graphicsConfigType, typeof(SmoothingMode), typeof(Size)
                    });
                }

                IsAvailable = _dwgReadMethod != null && _createAutoSizedBitmapMethod != null;
            }
            catch
            {
                IsAvailable = false;
            }
        }

        private static string? FindDriverDirectory()
        {
            // 查找优先级：
            // 1. 插件所在执行目录
            // 2. 插件子目录 Drivers/
            // 3. 开发环境 References/ 目录
            string[] probePaths = {
                AppDomain.CurrentDomain.BaseDirectory,
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Drivers"),
                Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "Drivers"),
                @"D:\Develop\QuickLook.Plugin.DwgsViewer\References"
            };

            foreach (var dir in probePaths)
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    if (File.Exists(Path.Combine(dir, "WW.Cad.dll")))
                        return dir;
                }
            }
            return null;
        }

        public Bitmap? RenderThumbnail(string filePath, int width, int height, bool isDark)
        {
            if (!IsAvailable || string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                object? model = (ext == ".dxf" && _dxfReadMethod != null)
                    ? _dxfReadMethod.Invoke(null, new object[] { filePath })
                    : _dwgReadMethod?.Invoke(null, new object[] { filePath });

                if (model == null) return null;

                object? cfg = CreateGraphicsConfig(isDark);
                object? identity = _matrixIdentityField?.GetValue(null);

                if (cfg != null && identity != null && _createAutoSizedBitmapMethod != null)
                {
                    return (Bitmap?)_createAutoSizedBitmapMethod.Invoke(null, new object[] {
                        model, identity, cfg, SmoothingMode.AntiAlias, new Size(width, height)
                    });
                }
            }
            catch
            {
            }
            return null;
        }

        public bool Load(string filePath, bool isDark)
        {
            if (!IsAvailable || string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return false;

            Unload();
            _isDark = isDark;

            try
            {
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                _currentModel = (ext == ".dxf" && _dxfReadMethod != null)
                    ? _dxfReadMethod.Invoke(null, new object[] { filePath })
                    : _dwgReadMethod?.Invoke(null, new object[] { filePath });

                if (_currentModel == null) return false;

                _graphicsConfig = CreateGraphicsConfig(_isDark);
                if (_gdiGraphics3DType != null && _graphicsConfig != null)
                {
                    _cadGraphics = Activator.CreateInstance(_gdiGraphics3DType, new object[] { _graphicsConfig });
                    _createDrawablesMethod?.Invoke(_cadGraphics, new object[] { _currentModel });
                    return true;
                }
            }
            catch
            {
                Unload();
            }
            return false;
        }

        public void RenderDetail(Graphics g, Rectangle clientRect, float zoom, float panX, float panY)
        {
            if (!IsAvailable || _cadGraphics == null || _bounds3DType == null || clientRect.Width <= 0 || clientRect.Height <= 0)
                return;

            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;

                object? bounds = Activator.CreateInstance(_bounds3DType);
                _boundingBoxMethod?.Invoke(_cadGraphics, new object[] { bounds! });

                int w = Math.Max(1, clientRect.Width);
                int h = Math.Max(1, clientRect.Height);

                // 1. baseTransform = GetBoundsToScreenTransform(bounds, w, h, 10)
                object? baseTransform = _getBoundsTransformMethod?.Invoke(null, new object[] { bounds!, w, h, 10 });
                if (baseTransform == null) return;

                // 2. finalTransform = Translation(w/2 + panX, h/2 + panY, 0) * Scaling(zoom, zoom, 1) * Translation(-w/2, -h/2, 0) * baseTransform
                object? t1 = _translationMethod?.Invoke(null, new object[] { (double)(w / 2f + panX), (double)(h / 2f + panY), 0.0 });
                object? s = _scalingMethod?.Invoke(null, new object[] { (double)zoom, (double)zoom, 1.0 });
                object? t2 = _translationMethod?.Invoke(null, new object[] { -(double)(w / 2f), -(double)(h / 2f), 0.0 });

                object? transform = MultiplyMatrices(t1, s);
                transform = MultiplyMatrices(transform, t2);
                transform = MultiplyMatrices(transform, baseTransform);

                // 3. Draw
                _gdiDrawMethod?.Invoke(_cadGraphics, new object[] { g, clientRect, transform! });
            }
            catch
            {
            }
        }

        public void SetBackColor(bool isDark)
        {
            _isDark = isDark;
            if (!IsAvailable || _currentModel == null) return;

            try
            {
                _graphicsConfig = CreateGraphicsConfig(isDark);
                if (_gdiGraphics3DType != null && _graphicsConfig != null)
                {
                    _cadGraphics = Activator.CreateInstance(_gdiGraphics3DType, new object[] { _graphicsConfig });
                    _createDrawablesMethod?.Invoke(_cadGraphics, new object[] { _currentModel });
                }
            }
            catch
            {
            }
        }

        public void Unload()
        {
            _cadGraphics = null;
            _graphicsConfig = null;
            _currentModel = null;
        }

        public void Dispose()
        {
            Unload();
        }

        private object? CreateGraphicsConfig(bool isDark)
        {
            if (_graphicsConfigType == null) return null;
            object? cfg = Activator.CreateInstance(_graphicsConfigType);
            if (cfg == null) return null;

            object? backColor = isDark
                ? _argbCtor?.Invoke(new object[] { 0, 0, 0 })
                : _argbCtor?.Invoke(new object[] { 255, 255, 255 });

            _cfgBackColorProp?.SetValue(cfg, backColor, null);
            _cfgColorCorrectProp?.SetValue(cfg, true, null);
            return cfg;
        }

        private object? MultiplyMatrices(object? m1, object? m2)
        {
            if (m1 == null || m2 == null) return null;
            return _matrixMultiplyMethod?.Invoke(null, new[] { m1, m2 });
        }
    }
}
