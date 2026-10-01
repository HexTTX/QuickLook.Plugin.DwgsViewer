using System;
using System.Drawing;

namespace QuickLook.Plugin.DwgsViewer.Core.CadEngine
{
    /// <summary>
    /// CAD 统一渲染引擎抽象接口。
    /// 支持动态驱动加载与多引擎热插拔（纯开源引擎 / 外部驱动 / 系统外壳）。
    /// </summary>
    internal interface ICadEngine : IDisposable
    {
        /// <summary>引擎显示名称</summary>
        string Name { get; }

        /// <summary>当前环境是否可用</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// 渲染图纸缩略图（供多图宫格流使用）
        /// </summary>
        Bitmap? RenderThumbnail(string filePath, int width, int height, bool isDark);

        /// <summary>
        /// 加载图纸（供详情大图交互视图使用）
        /// </summary>
        bool Load(string filePath, bool isDark);

        /// <summary>
        /// 详情视图 GDI+ 矢量绘制（支持缩放与平移）
        /// </summary>
        void RenderDetail(Graphics g, Rectangle clientRect, float zoom, float panX, float panY);

        /// <summary>
        /// 切换底色
        /// </summary>
        void SetBackColor(bool isDark);

        /// <summary>
        /// 卸载当前图纸并释放资源
        /// </summary>
        void Unload();
    }
}
