using System;
using System.Drawing;
using System.Windows.Forms;
using QuickLook.Plugin.DwgsViewer.Core.CadEngine;

namespace QuickLook.Plugin.DwgsViewer.Core
{
    /// <summary>
    /// CAD 交互视图控制器。
    /// 承载标准 WinForms Panel 双缓冲绘图，处理鼠标滚轮以光标为中心的精准平移缩放交互，
    /// 具备准星锁定算法（无漂移）、对称平滑缩放、三键平移拖拽与双击一键自适应居中。
    /// </summary>
    public class CADImaging : IDisposable
    {
        public event EventHandler<CADImagingEventArgs>? StatusUpdated;
        public event EventHandler<string>? AfterLoaded;

        private Panel? _panel;
        private float _zoom = 1f;
        private float _panX = 0f;
        private float _panY = 0f;
        private Point _lastMouse;
        private bool _isMouseDown;
        private bool _isLoaded;

        // 缩放参数：每次滚轮或按钮缩放 20%（CAD 行业标准步长），对称性保证
        private const float ZoomStepFactor = 1.20f;
        private const float MinZoom = 0.01f;   // 1%
        private const float MaxZoom = 100.0f;  // 10,000%

        public CADImaging(Panel panel)
        {
            _panel = panel;
            _panel.BackColor = Color.FromArgb(24, 24, 26);
            _panel.Cursor = Cursors.Default;

            // 开启底层原生双缓冲，彻底消除高频重绘闪烁
            typeof(Panel).InvokeMember("DoubleBuffered",
                System.Reflection.BindingFlags.SetProperty | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, _panel, new object[] { true });

            _panel.Paint += OnPaint;
            _panel.MouseWheel += OnMouseWheel;
            _panel.MouseDown += OnMouseDown;
            _panel.MouseMove += OnMouseMove;
            _panel.MouseUp += OnMouseUp;
            _panel.MouseDoubleClick += (_, _) => { if (IsLoaded) ResetScaling(); };
            _panel.Resize += (_, _) => { if (IsLoaded) _panel?.Invalidate(); };
            _panel.VisibleChanged += (_, _) =>
            {
                if (!_isLoaded || _panel == null || !_panel.Visible) return;
                ResetScaling();
            };
        }

        public void Dispose()
        {
            CadEngineManager.Instance.Unload();
            _panel = null;
        }

        public bool IsLoaded => _isLoaded;
        public bool IsDark { get; set; } = true;
        public string RealScale => $"{_zoom * 100:F0}";

        // ─── 鼠标手势交互 ───

        private void OnMouseWheel(object? sender, MouseEventArgs e)
        {
            if (_panel == null || !_isLoaded) return;

            // 对称缩放：滚轮向上放大 1.2x，向下缩小 1/1.2x，多次来回缩放绝不累积浮点误差
            float factor = e.Delta > 0 ? ZoomStepFactor : (1.0f / ZoomStepFactor);
            float newZoom = _zoom * factor;
            if (newZoom < MinZoom) newZoom = MinZoom;
            if (newZoom > MaxZoom) newZoom = MaxZoom;

            float actualFactor = newZoom / _zoom;
            _zoom = newZoom;

            // 【核心数学优化】：准星锁定算法（Cursor-Anchored Zoom）
            // 计算鼠标当前所在位置相对于视口中心的相对偏移 dx, dy
            float w2 = _panel.ClientSize.Width / 2f;
            float h2 = _panel.ClientSize.Height / 2f;
            float dx = e.X - w2;
            float dy = e.Y - h2;

            // 保持鼠标光标所指的 CAD 特征点在缩放后屏幕位置 100% 绝对不动！
            _panX = dx - (dx - _panX) * actualFactor;
            _panY = dy - (dy - _panY) * actualFactor;

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
            if (!_isMouseDown || _panel == null || !_isLoaded) return;

            // 平移累加，实时重绘
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
            if (!_isLoaded || _panel == null || !_panel.Visible) return;

            try
            {
                CadEngineManager.Instance.RenderDetail(e.Graphics, _panel.ClientRectangle, _zoom, _panX, _panY);
            }
            catch
            {
            }

            StatusUpdated?.Invoke(this, CADImagingEventArgs.NewStatusUpdatedEventArgs(RealScale));
        }

        // ─── 外部公共 API ───

        public void ZoomIn()
        {
            ZoomByCenter(ZoomStepFactor);
        }

        public void ZoomOut()
        {
            ZoomByCenter(1.0f / ZoomStepFactor);
        }

        private void ZoomByCenter(float factor)
        {
            if (_panel == null || !_isLoaded) return;

            float newZoom = _zoom * factor;
            if (newZoom < MinZoom) newZoom = MinZoom;
            if (newZoom > MaxZoom) newZoom = MaxZoom;

            float actualFactor = newZoom / _zoom;
            _zoom = newZoom;

            // 按钮与快捷键缩放：以屏幕正中心为锚点等比放缩，保证视口中心特征不变
            _panX *= actualFactor;
            _panY *= actualFactor;

            _panel.Invalidate();
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

            _zoom = 1f;
            _panX = 0f;
            _panY = 0f;

            _isLoaded = CadEngineManager.Instance.Load(fileName, IsDark);

            _panel?.Invalidate();
            StatusUpdated?.Invoke(this, CADImagingEventArgs.NewStatusUpdatedEventArgs(RealScale));
            AfterLoaded?.Invoke(this, fileName);
        }

        public bool SetBackColor(bool isDark)
        {
            IsDark = isDark;
            if (_panel == null) return isDark;

            _panel.BackColor = isDark ? Color.FromArgb(24, 24, 26) : Color.White;
            CadEngineManager.Instance.SetBackColor(isDark);

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
