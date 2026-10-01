using System;
using System.Drawing;

namespace QuickLook.Plugin.DwgsViewer.Core.CadEngine
{
    /// <summary>
    /// CAD 引擎统一管理器（单例）。
    /// 四级引擎自适应调度：
    /// 1. WoutWare CadLib（WW.Cad.dll 动态驱动，纯实例无锁极速渲染）
    /// 2. CADSoftTools（CADImport.dll 动态驱动，内置试用补丁与多线程防崩锁）
    /// 3. ACadSharp 原生开源矢量引擎（100% MIT 开源合规，图块展开、凸度圆角、中文排版）
    /// 4. Built-in 原生文件头与系统外壳引擎（0.3ms DWG 二进制预览位图提取 + Windows Shell 保底）
    /// </summary>
    public class CadEngineManager : IDisposable
    {
        private static readonly Lazy<CadEngineManager> _instance = new Lazy<CadEngineManager>(() => new CadEngineManager());
        public static CadEngineManager Instance => _instance.Value;

        private readonly ICadEngine _wwEngine;
        private readonly ICadEngine _cadImportEngine;
        private readonly ICadEngine _acadSharpEngine;
        private readonly ICadEngine _builtinEngine;

        private ICadEngine _activeDetailEngine;

        public bool IsWwAvailable => _wwEngine.IsAvailable;
        public bool IsCadImportAvailable => _cadImportEngine.IsAvailable;
        public bool IsHighFidelityAvailable => _wwEngine.IsAvailable || _cadImportEngine.IsAvailable || _acadSharpEngine.IsAvailable;

        public string ActiveEngineName
        {
            get
            {
                if (_wwEngine.IsAvailable) return _wwEngine.Name;
                if (_cadImportEngine.IsAvailable) return _cadImportEngine.Name;
                if (_acadSharpEngine.IsAvailable) return _acadSharpEngine.Name;
                return _builtinEngine.Name;
            }
        }

        private CadEngineManager()
        {
            _wwEngine = new CadLibDynamicEngine();
            _cadImportEngine = new CadImportDynamicEngine();
            _acadSharpEngine = new ACadSharpEngine();
            _builtinEngine = new BuiltinCadEngine();

            if (_wwEngine.IsAvailable)
                _activeDetailEngine = _wwEngine;
            else if (_cadImportEngine.IsAvailable)
                _activeDetailEngine = _cadImportEngine;
            else if (_acadSharpEngine.IsAvailable)
                _activeDetailEngine = _acadSharpEngine;
            else
                _activeDetailEngine = _builtinEngine;
        }

        /// <summary>
        /// 渲染图纸缩略图（四级流水线自动探测与降级）
        /// </summary>
        public Bitmap? RenderThumbnail(string filePath, int width, int height, bool isDark)
        {
            // 1. WoutWare 驱动
            if (_wwEngine.IsAvailable)
            {
                var bmp = _wwEngine.RenderThumbnail(filePath, width, height, isDark);
                if (bmp != null) return bmp;
            }

            // 2. CADSoftTools 驱动
            if (_cadImportEngine.IsAvailable)
            {
                var bmp = _cadImportEngine.RenderThumbnail(filePath, width, height, isDark);
                if (bmp != null) return bmp;
            }

            // 3. ACadSharp 原生开源矢量引擎
            if (_acadSharpEngine.IsAvailable)
            {
                var bmp = _acadSharpEngine.RenderThumbnail(filePath, width, height, isDark);
                if (bmp != null) return bmp;
            }

            // 4. 内置 DWG 文件头 / Windows Shell 降级
            return _builtinEngine.RenderThumbnail(filePath, width, height, isDark);
        }

        /// <summary>
        /// 加载图纸供详情大图交互视图使用
        /// </summary>
        public bool Load(string filePath, bool isDark)
        {
            // 1. WoutWare 驱动
            if (_wwEngine.IsAvailable && _wwEngine.Load(filePath, isDark))
            {
                _activeDetailEngine = _wwEngine;
                return true;
            }

            // 2. CADSoftTools 驱动
            if (_cadImportEngine.IsAvailable && _cadImportEngine.Load(filePath, isDark))
            {
                _activeDetailEngine = _cadImportEngine;
                return true;
            }

            // 3. ACadSharp 原生开源矢量引擎
            if (_acadSharpEngine.IsAvailable && _acadSharpEngine.Load(filePath, isDark))
            {
                _activeDetailEngine = _acadSharpEngine;
                return true;
            }

            // 4. 内置引擎降级
            if (_builtinEngine.Load(filePath, isDark))
            {
                _activeDetailEngine = _builtinEngine;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 详情视图 GDI+ 绘制
        /// </summary>
        public void RenderDetail(Graphics g, Rectangle clientRect, float zoom, float panX, float panY)
        {
            _activeDetailEngine.RenderDetail(g, clientRect, zoom, panX, panY);
        }

        /// <summary>
        /// 切换底色
        /// </summary>
        public void SetBackColor(bool isDark)
        {
            _wwEngine.SetBackColor(isDark);
            _cadImportEngine.SetBackColor(isDark);
            _acadSharpEngine.SetBackColor(isDark);
            _builtinEngine.SetBackColor(isDark);
        }

        /// <summary>
        /// 卸载当前图纸
        /// </summary>
        public void Unload()
        {
            _wwEngine.Unload();
            _cadImportEngine.Unload();
            _acadSharpEngine.Unload();
            _builtinEngine.Unload();
        }

        public void Dispose()
        {
            _wwEngine.Dispose();
            _cadImportEngine.Dispose();
            _acadSharpEngine.Dispose();
            _builtinEngine.Dispose();
        }
    }
}
