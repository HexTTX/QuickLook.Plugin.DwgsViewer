using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using WW.Cad.Drawing;
using WW.Cad.Drawing.GDI;
using WW.Cad.IO;
using WW.Cad.Model;
using WW.Drawing;
using WW.Math;
using Color = System.Drawing.Color;

namespace QuickLook.Plugin.DwgsViewer.Core
{
    /// <summary>
    /// 基于 CadLib (WW.Cad) 的 CAD 纯矢量渲染引擎。
    /// 纯实例架构，无静态全局状态，天然线程安全，无水印。
    /// 完全替代旧版 CADImport 引擎。
    /// </summary>
    public class CADImaging : IDisposable
    {
        public event EventHandler<CADImagingEventArgs>? StatusUpdated;
        public event EventHandler<string>? AfterLoaded;

        private Panel? _panel;
        private DxfModel? _model;
        private GDIGraphics3D? _cadGraphics;
        private GraphicsConfig? _graphicsConfig;

        private float _zoom = 1f;
        private float _panX = 0f;
        private float _panY = 0f;
        private Point _lastMouse;
        private bool _isMouseDown;

        public CADImaging(Panel panel)
        {
            _panel = panel;
            _panel.BackColor = Color.FromArgb(24, 24, 26);
            _panel.Cursor = Cursors.Default;

            // 开启双缓冲防闪烁
            typeof(Panel).InvokeMember("DoubleBuffered",
                System.Reflection.BindingFlags.SetProperty | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, _panel, new object[] { true });

            _panel.Paint += OnPaint;
            _panel.MouseWheel += OnMouseWheel;
            _panel.MouseDown += OnMouseDown;
            _panel.MouseMove += OnMouseMove;
            _panel.MouseUp += OnMouseUp;
            _panel.MouseDoubleClick += (_, _) => { if (IsLoaded) ResetScaling(); };
            _panel.VisibleChanged += (_, _) =>
            {
                if (_model == null || _panel == null || !_panel.Visible) return;
                ResetScaling();
            };
        }

        public void Dispose()
        {
            _cadGraphics = null;
            _graphicsConfig = null;
            _model = null;
            _panel = null;
        }

        public bool IsLoaded => _model != null;
        public bool IsDark { get; set; } = true;
        public string RealScale => $"{_zoom * 100:F0}";

        // ─── 鼠标手势交互 ───

        private void OnMouseWheel(object? sender, MouseEventArgs e)
        {
            if (_panel == null || _model == null) return;

            float factor = e.Delta > 0 ? 1.25f : 0.8f;
            _zoom *= factor;
            if (_zoom < 0.02f) _zoom = 0.02f;
            if (_zoom > 50f) _zoom = 50f;

            // 以鼠标指针当前所在位置为中心进行平移补偿
            _panX = e.X - (e.X - _panX) * factor;
            _panY = e.Y - (e.Y - _panY) * factor;

            _panel.Invalidate();
            StatusUpdated?.Invoke(this, CADImagingEventArgs.NewStatusUpdatedEventArgs(RealScale));
        }

        private void OnMouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle)
            {
                _lastMouse = e.Location;
                _isMouseDown = true;
                if (_panel != null) _panel.Cursor = Cursors.SizeAll;
            }
        }

        private void OnMouseMove(object? sender, MouseEventArgs e)
        {
            if (!_isMouseDown || _panel == null || _model == null) return;

            _panX += (e.X - _lastMouse.X);
            _panY += (e.Y - _lastMouse.Y);
            _lastMouse = e.Location;

            _panel.Invalidate();
        }

        private void OnMouseUp(object? sender, MouseEventArgs e)
        {
            _isMouseDown = false;
            if (_panel != null) _panel.Cursor = Cursors.Default;
        }

        // ─── GDI+ 矢量硬件渲染 ───

        private void OnPaint(object? sender, PaintEventArgs e)
        {
            if (_model == null || _cadGraphics == null || _panel == null || !_panel.Visible) return;

            try
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                var bounds = new Bounds3D();
                _cadGraphics.BoundingBox(bounds);

                if (bounds.Initialized)
                {
                    int w = Math.Max(1, _panel.ClientSize.Width);
                    int h = Math.Max(1, _panel.ClientSize.Height);

                    // 1. 基准自适应居中变换（带 10px 边距）
                    var baseTransform = Transformation4D.GetBoundsToScreenTransform(bounds, w, h, 10);

                    // 2. 缩放与平移变换
                    var finalTransform = Transformation4D.Translation(w / 2f + _panX, h / 2f + _panY, 0)
                                       * Transformation4D.Scaling(_zoom, _zoom, 1)
                                       * Transformation4D.Translation(-w / 2f, -h / 2f, 0)
                                       * baseTransform;

                    // 3. 极速 GDI+ 矢量绘制
                    _cadGraphics.Draw(g, _panel.ClientRectangle, finalTransform);
                }
            }
            catch
            {
            }

            StatusUpdated?.Invoke(this, CADImagingEventArgs.NewStatusUpdatedEventArgs(RealScale));
        }

        // ─── 外部公共 API ───

        public void ZoomIn()
        {
            _zoom *= 1.25f;
            if (_zoom > 50f) _zoom = 50f;
            _panel?.Invalidate();
            StatusUpdated?.Invoke(this, CADImagingEventArgs.NewStatusUpdatedEventArgs(RealScale));
        }

        public void ZoomOut()
        {
            _zoom *= 0.8f;
            if (_zoom < 0.02f) _zoom = 0.02f;
            _panel?.Invalidate();
            StatusUpdated?.Invoke(this, CADImagingEventArgs.NewStatusUpdatedEventArgs(RealScale));
        }

        public void ResetScaling()
        {
            _zoom = 1f;
            _panX = 0f;
            _panY = 0f;
            _panel?.Invalidate();
            StatusUpdated?.Invoke(this, CADImagingEventArgs.NewStatusUpdatedEventArgs(RealScale));
        }

        public void Resize()
        {
            _panel?.Invalidate();
        }

        public void LoadFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return;

            _model = null;
            _cadGraphics = null;
            _zoom = 1f;
            _panX = 0f;
            _panY = 0f;

            try
            {
                string ext = Path.GetExtension(fileName).ToLowerInvariant();
                _model = (ext == ".dxf") ? DxfReader.Read(fileName) : DwgReader.Read(fileName);

                _graphicsConfig = new GraphicsConfig();
                _graphicsConfig.CorrectColorForBackgroundColor = true;
                _graphicsConfig.BackColor = IsDark ? new ArgbColor(24, 24, 26) : new ArgbColor(255, 255, 255);

                _cadGraphics = new GDIGraphics3D(_graphicsConfig);
                _cadGraphics.CreateDrawables(_model);
            }
            catch
            {
                _model = null;
                _cadGraphics = null;
            }

            _panel?.Invalidate();
            StatusUpdated?.Invoke(this, CADImagingEventArgs.NewStatusUpdatedEventArgs(RealScale));
            AfterLoaded?.Invoke(this, fileName);
        }

        public bool SetBackColor(bool isDark)
        {
            IsDark = isDark;
            if (_panel == null) return isDark;

            _panel.BackColor = isDark ? Color.FromArgb(24, 24, 26) : Color.White;

            if (_graphicsConfig != null)
            {
                _graphicsConfig.BackColor = isDark ? new ArgbColor(24, 24, 26) : new ArgbColor(255, 255, 255);
            }

            if (_model != null)
            {
                _cadGraphics = new GDIGraphics3D(_graphicsConfig!);
                _cadGraphics.CreateDrawables(_model);
            }

            _panel.Invalidate();
            return isDark;
        }

        public bool SetDrawingColors(bool val)
        {
            _panel?.Invalidate();
            return val;
        }

        public bool SetTextVisible(bool val)
        {
            _panel?.Invalidate();
            return val;
        }
    }

    public class CADImagingEventArgs : EventArgs
    {
        public enum EventType
        {
            None,
            Status
        }

        public EventType Type { get; set; }
        public object[] Arguments { get; set; }

        public CADImagingEventArgs(EventType type, object[] args)
        {
            Type = type;
            Arguments = args;
        }

        public static CADImagingEventArgs NewStatusUpdatedEventArgs(params object[] texts)
        {
            return new CADImagingEventArgs(EventType.Status, texts);
        }
    }
}
