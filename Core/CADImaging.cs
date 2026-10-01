using System;
using System.Drawing;
using System.Windows.Forms;
using QuickLook.Plugin.DwgsViewer.Core.CadEngine;

namespace QuickLook.Plugin.DwgsViewer.Core
{
    /// <summary>
    /// CAD 交互视图控制器。
    /// 承载标准 WinForms Panel 双缓冲绘图，处理鼠标滚轮以光标为中心的平移缩放交互，
    /// 底层渲染由 CadEngineManager 统一管理与热插拔调度。
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
            if (!_isMouseDown || _panel == null || !_isLoaded) return;

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
