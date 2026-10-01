using System;
using System.Drawing;

namespace QuickLook.Plugin.DwgsViewer.Core.CadEngine
{
    /// <summary>
    /// CAD 引擎统一管理器（单例）。
    /// 优先级调度：
    /// 1. CadLibDynamicEngine（外部可选动态驱动，若用户自行放置 WW.Cad.dll 则激活）
    /// 2. ACadSharpEngine（默认原生开源矢量引擎，100% MIT 纯净合规，支持图块递归展开与中英文字体）
    /// 3. BuiltinCadEngine（DWG 二进制文件头 0.3ms 极速提取 + Windows Shell 降级保底）
    /// </summary>
    public class CadEngineManager : IDisposable
    {
        private static readonly Lazy<CadEngineManager> _instance = new Lazy<CadEngineManager>(() => new CadEngineManager());
        public static CadEngineManager Instance => _instance.Value;

        private readonly ICadEngine _dynamicEngine;
        private readonly ICadEngine _acadSharpEngine;
        private readonly ICadEngine _builtinEngine;

        private ICadEngine _activeDetailEngine;

        public bool IsHighFidelityAvailable => _dynamicEngine.IsAvailable || _acadSharpEngine.IsAvailable;

        public string ActiveEngineName
        {
            get
            {
                if (_dynamicEngine.IsAvailable) return _dynamicEngine.Name;
                if (_acadSharpEngine.IsAvailable) return _acadSharpEngine.Name;
                return _builtinEngine.Name;
            }
        }

        private CadEngineManager()
        {
            _dynamicEngine = new CadLibDynamicEngine();
            _acadSharpEngine = new ACadSharpEngine();
            _builtinEngine = new BuiltinCadEngine();

            if (_dynamicEngine.IsAvailable)
                _activeDetailEngine = _dynamicEngine;
            else if (_acadSharpEngine.IsAvailable)
                _activeDetailEngine = _acadSharpEngine;
            else
                _activeDetailEngine = _builtinEngine;
        }

        /// <summary>
        /// 渲染图纸缩略图（优先动态高保真矢量引擎 -> 原生开源矢量引擎 -> 内置文件头引擎）
        /// </summary>
        public Bitmap? RenderThumbnail(string filePath, int width, int height, bool isDark)
        {
            // 1. 尝试动态驱动
            if (_dynamicEngine.IsAvailable)
            {
                var bmp = _dynamicEngine.RenderThumbnail(filePath, width, height, isDark);
                if (bmp != null) return bmp;
            }

            // 2. 默认使用 100% 开源 ACadSharp 原生矢量引擎
            if (_acadSharpEngine.IsAvailable)
            {
                var bmp = _acadSharpEngine.RenderThumbnail(filePath, width, height, isDark);
                if (bmp != null) return bmp;
            }

            // 3. 降级使用内置 DWG 文件头 / Windows Shell
            return _builtinEngine.RenderThumbnail(filePath, width, height, isDark);
        }

        /// <summary>
        /// 加载图纸供详情大图交互视图使用
        /// </summary>
        public bool Load(string filePath, bool isDark)
        {
            // 1. 尝试动态驱动
            if (_dynamicEngine.IsAvailable && _dynamicEngine.Load(filePath, isDark))
            {
                _activeDetailEngine = _dynamicEngine;
                return true;
            }

            // 2. 默认使用 100% 开源 ACadSharp 原生矢量引擎
            if (_acadSharpEngine.IsAvailable && _acadSharpEngine.Load(filePath, isDark))
            {
                _activeDetailEngine = _acadSharpEngine;
                return true;
            }

            // 3. 降级使用内置文件头/Shell
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
            _dynamicEngine.SetBackColor(isDark);
            _acadSharpEngine.SetBackColor(isDark);
            _builtinEngine.SetBackColor(isDark);
        }

        /// <summary>
        /// 卸载当前图纸
        /// </summary>
        public void Unload()
        {
            _dynamicEngine.Unload();
            _acadSharpEngine.Unload();
            _builtinEngine.Unload();
        }

        public void Dispose()
        {
            _dynamicEngine.Dispose();
            _acadSharpEngine.Dispose();
            _builtinEngine.Dispose();
        }
    }
}
