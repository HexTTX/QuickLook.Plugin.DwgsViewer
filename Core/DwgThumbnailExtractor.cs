using System.Drawing;
using QuickLook.Plugin.DwgsViewer.Core.CadEngine;

namespace QuickLook.Plugin.DwgsViewer.Core
{
    /// <summary>
    /// DWG / DXF 缩略图统一调度接口。
    /// 统一委托 CadEngineManager 多级引擎（优先动态驱动，自动降级至内置文件头与系统外壳）。
    /// </summary>
    internal static class DwgThumbnailExtractor
    {
        public static Bitmap? RenderCadDrawing(string filePath, int width, int height, bool isDark)
        {
            return CadEngineManager.Instance.RenderThumbnail(filePath, width, height, isDark);
        }

        public static Bitmap? ExtractThumbnail(string filePath, int targetSize = 256)
        {
            return CadEngineManager.Instance.RenderThumbnail(filePath, targetSize, (int)(targetSize * 0.75), true);
        }
    }
}
