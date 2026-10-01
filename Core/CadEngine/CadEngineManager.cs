using System;
using System.Drawing;

namespace QuickLook.Plugin.DwgsViewer.Core.CadEngine
{
    /// <summary>
    /// CAD 引擎统一管理器（单例）。
    /// 负责多引擎优先级调度、动态热插拔与故障自动降级。
    /// </summary>
    public class CadEngineManager : IDisposable
    {
        private static readonly Lazy<CadEngineManager> _instance = new Lazy<CadEngineManager>(() => new CadEngineManager());
        public static CadEngineManager Instance => _instance.Value;

        private readonly ICadEngine _dynamicEngine;
        private readonly ICadEngine _builtinEngine;

        private ICadEngine _activeDetailEngine;

        public bool IsHighFidelityAvailable => _dynamicEngine.IsAvailable;
        public string ActiveEngineName => _dynamicEngine.IsAvailable ? _dynamicEngine.Name : _builtinEngine.Name;

        private CadEngineManager()
        {
            _dynamicEngine = new CadLibDynamicEngine();
            _builtinEngine = new BuiltinCadEngine();

            _activeDetailEngine = _dynamicEngine.IsAvailable ? _dynamicEngine : _builtinEngine;
        }

        /// <summary>
        /// 渲染图纸缩略图（优先动态高保真矢量引擎，失败自动降级到内置文件头引擎）
        /// </summary>
        public Bitmap? RenderThumbnail(string filePath, int width, int height, bool isDark)
        {
            if (_dynamicEngine.IsAvailable)
            {
                var bmp = _dynamicEngine.RenderThumbnail(filePath, width, height, isDark);
                if (bmp != null) return bmp;
            }

            // 降级使用内置文件头 / Windows Shell
            return _builtinEngine.RenderThumbnail(filePath, width, height, isDark);
        }

        /// <summary>
        /// 加载图纸供详情大图交互视图使用
        /// </summary>
        public bool Load(string filePath, bool isDark)
        {
            if (_dynamicEngine.IsAvailable && _dynamicEngine.Load(filePath, isDark))
            {
                _activeDetailEngine = _dynamicEngine;
                return true;
            }

            // 降级使用内置引擎
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
            _builtinEngine.SetBackColor(isDark);
        }

        /// <summary>
        /// 卸载当前图纸
        /// </summary>
        public void Unload()
        {
            _dynamicEngine.Unload();
            _builtinEngine.Unload();
        }

        public void Dispose()
        {
            _dynamicEngine.Dispose();
            _builtinEngine.Dispose();
        }
    }
}
