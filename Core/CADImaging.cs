using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using CADImport;
using CADImport.FaceModule;
using CADImport.RasterImage;
using ScrollOrientation = CADImport.FaceModule.ScrollOrientation;

namespace QuickLook.Plugin.DwgsViewer.Core
{
    public class CADImaging : IDisposable
    {
        public event EventHandler<CADImagingEventArgs>? StatusUpdated;
        public event EventHandler<CADImagingEventArgs>? CursorUpdated;
        public event EventHandler<CADImagingEventArgs>? RealPointUpdated;
        public event EventHandler<CADImagingEventArgs>? OffsetPointUpdated;
        public event EventHandler<string>? AfterLoaded;

        private CADPictureBox? cadPictBox;
        private CADImage? cadImage;
        private DPoint originalPoint = new DPoint(default, default, default);
        private readonly ClipRect? clipRectangle;
        private PointF positionPrev;
        private PointF position;
        private float imageScalePrev = 1f;
        private float imageScale = 1f;
        private SizeF visibleArea;
        private int currentXClickPosition;
        private int currentYClickPosition;
        private bool isMouseDown;
        private bool textVisible = true;
        private bool drawingColor = true;

        public CADImaging(CADPictureBox cadPictureBox)
        {
            cadPictBox = cadPictureBox;
            cadPictBox.BackColor = Color.Black;
            cadPictBox.BorderStyle = BorderStyle.None;
            cadPictBox.Cursor = Cursors.Default;
            cadPictBox.DoubleBuffering = true;
            cadPictBox.Ortho = false;
            cadPictBox.ScrollBars = ScrollBarsShow.Automatic;
            cadPictBox.Size = new Size(1000, 1000);
            cadPictBox.TabStop = false;
            cadPictBox.Dock = DockStyle.Fill;
            clipRectangle = new ClipRect(cadPictureBox)
            {
                MultySelect = false,
            };

            cadPictBox.Paint += OnCADPictBoxPaint;
            cadPictBox.MouseWheel += OnCADPictBoxMouseWheel;
            cadPictBox.ScrollEvent += OnCADPictBoxScroll;
            cadPictBox.MouseDown += OnCADPictBoxMouseDown;
            cadPictBox.MouseMove += OnCADPictBoxMouseMove;
            cadPictBox.MouseUp += OnCADPictBoxMouseUp;
            cadPictBox.MouseDoubleClick += OnCADPictBoxMouseDoubleClick;
            cadPictBox.VisibleChanged += (_, _) =>
            {
                if (cadImage == null || cadPictBox == null)
                    return;

                if (!cadPictBox.Visible)
                    return;

                ResetScaling();
                StatusUpdated?.Invoke(this, CADImagingEventArgs.NewStatusUpdatedEventArgs(RealScale));
            };
        }

        public void Dispose()
        {
            cadPictBox = null;
            cadImage?.Dispose();
            cadImage = null;
        }

        public CADImage? CADImage => cadImage;
        public bool IsLoaded => cadImage != null;
        public bool IsDark { get; set; } = true;
        public bool IsNormalDrawMode => cadImage != null && cadImage.DrawMode == CADDrawMode.Normal;
        public bool IsBlackBackColor => cadPictBox != null && cadPictBox.BackColor == Color.Black;

        private float LeftImagePosition
        {
            get => position.X;
            set => position.X = value;
        }

        private float TopImagePosition
        {
            get => position.Y;
            set => position.Y = value;
        }

        private RectangleF ImageRectangleF => new RectangleF(LeftImagePosition, TopImagePosition, visibleArea.Width * imageScale, visibleArea.Height * imageScale);

        public string RealScale
        {
            get
            {
                if (cadImage != null && cadImage.AbsWidth > 0)
                    return string.Format("{0,2:F}", visibleArea.Width * imageScale / cadImage.AbsWidth * cadImage.MMToPixelX * 100);
                else
                    return string.Format("{0}", imageScale);
            }
        }

        private void OnCADPictBoxMouseWheel(object sender, MouseEventArgs e)
        {
            if (e.Delta < 0)
                Zoom(0.75f);
            else
                Zoom(1.33f);

            Shift();
            SetPictureBoxPosition(position);
        }

        private void OnCADPictBoxScroll(object sender, ScrollEventArgsExt e)
        {
            if (e.NewValue == 0 && e.OldValue == 0)
                e.NewValue = -5;

            if (e.ScrollOrientation == ScrollOrientation.VerticalScroll)
                TopImagePosition -= e.NewValue - e.OldValue;

            if (e.ScrollOrientation == ScrollOrientation.HorizontalScroll)
                LeftImagePosition -= e.NewValue - e.OldValue;

            cadPictBox?.Invalidate();
        }

        private void OnCADPictBoxMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle || e.Button == MouseButtons.Left)
            {
                currentXClickPosition = e.X;
                currentYClickPosition = e.Y;
                isMouseDown = true;
                if (cadPictBox != null) cadPictBox.Cursor = Cursors.SizeAll;
            }
        }

        private void OnCADPictBoxMouseUp(object sender, MouseEventArgs e)
        {
            isMouseDown = false;
            if (cadPictBox != null) cadPictBox.Cursor = Cursors.Default;
            cadPictBox?.Invalidate();
        }

        private void OnCADPictBoxMouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (!IsLoaded) return;
            ResetScaling();
        }

        private void OnCADPictBoxMouseMove(object sender, MouseEventArgs e)
        {
            if (cadImage == null || cadPictBox == null) return;

            if (isMouseDown)
            {
                position.X -= currentXClickPosition - e.X;
                position.Y -= currentYClickPosition - e.Y;
                currentXClickPosition = e.X;
                currentYClickPosition = e.Y;
                cadPictBox.Invalidate();
                SetPictureBoxPosition(position);
            }
            positionPrev = new PointF(e.X, e.Y);
        }

        private void OnCADPictBoxPaint(object sender, PaintEventArgs e)
        {
            if (cadImage == null || cadPictBox == null) return;
            if (!cadPictBox.Visible) return;

            DrawCADImage(e.Graphics);
            StatusUpdated?.Invoke(this, CADImagingEventArgs.NewStatusUpdatedEventArgs(RealScale));
        }

        private void Shift()
        {
            LeftImagePosition = positionPrev.X - (positionPrev.X - LeftImagePosition) * imageScale / imageScalePrev;
            TopImagePosition = positionPrev.Y - (positionPrev.Y - TopImagePosition) * imageScale / imageScalePrev;
            imageScalePrev = imageScale;
        }

        private void DrawCADImage(Graphics g)
        {
            try
            {
                Shift();
                RectangleF tmp = ImageRectangleF;
                SetSizePictureBox(new Size((int)tmp.Width, (int)tmp.Height));
                SetPictureBoxPosition(position);
                cadImage?.Draw(g, tmp, cadPictBox);
            }
            catch
            {
            }
        }

        private void SetSizePictureBox(Size sz)
        {
            if (cadPictBox == null) return;

            if (((position.X < 0) || (position.Y < 0) ||
                 (position.X + sz.Width > cadPictBox.Width) ||
                 (position.Y + sz.Height > cadPictBox.Height)) && IsSizeWithInBox())
            {
                if (position.X < 0)
                    sz.Width = (int)(cadPictBox.Width - position.X);

                if (position.Y < 0)
                    sz.Height = (int)(cadPictBox.Height - position.Y);

                if (position.X + sz.Width > cadPictBox.Width)
                    sz.Width = (int)(cadPictBox.Width + position.X);

                if (position.Y + sz.Height > cadPictBox.Height)
                    sz.Height = (int)(cadPictBox.Height + position.Y);
            }
            cadPictBox.SetVirtualSizeNoInvalidate(sz);
        }

        private bool IsSizeWithInBox()
        {
            if (cadPictBox == null) return false;
            RectangleF tmp = ImageRectangleF;
            return (tmp.Width <= cadPictBox.Size.Width) || (tmp.Height <= cadPictBox.Height);
        }

        public void Resize()
        {
            if (cadImage == null || cadPictBox == null) return;
            if (cadPictBox.ClientRectangle.Height == 0 || cadImage.AbsHeight == 0) return;

            float wh = (float)(cadImage.AbsWidth / cadImage.AbsHeight);
            float new_wh = (float)cadPictBox.ClientRectangle.Width / cadPictBox.ClientRectangle.Height;

            if (cadImage is CADRasterImage)
                visibleArea = new SizeF((float)cadImage.AbsWidth, (float)cadImage.AbsHeight);
            else
                visibleArea = cadPictBox.Size;

            if (new_wh > wh)
                visibleArea.Width = visibleArea.Height * wh;
            else
            {
                if (new_wh < wh)
                    visibleArea.Height = visibleArea.Width / wh;
                else
                    visibleArea = cadPictBox.Size;
            }
            LeftImagePosition = (cadPictBox.ClientRectangle.Width - visibleArea.Width) / 2f;
            TopImagePosition = (cadPictBox.ClientRectangle.Height - visibleArea.Height) / 2f;
            cadPictBox.Invalidate();
        }

        private void SetPictureBoxPosition(PointF value)
        {
            try
            {
                if (cadPictBox == null) return;

                int w1 = value.X > 0 ? 0 : (int)Math.Abs(value.X);
                if (w1 > cadPictBox.VirtualSize.Width)
                    w1 = cadPictBox.VirtualSize.Width;

                int h1 = value.Y > 0 ? 0 : (int)Math.Abs(value.Y);
                if (h1 > cadPictBox.VirtualSize.Height)
                    h1 = cadPictBox.VirtualSize.Height;

                cadPictBox.SetPositionNoInvalidate(new Point(w1, h1));
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.ToString());
            }
        }

        public void Zoom(float i)
        {
            if (cadImage == null) return;
            imageScale *= i;
            if (imageScale < 0.005f) imageScale = 0.005f;
            cadPictBox?.Invalidate();
        }

        public void ZoomIn() => Zoom(1.33f);
        public void ZoomOut() => Zoom(0.75f);

        public void ResetScaling()
        {
            if (cadPictBox == null) return;
            imageScale = 1f;
            imageScalePrev = 1f;
            Resize();
            LeftImagePosition = (cadPictBox.ClientRectangle.Width - visibleArea.Width) / 2f;
            TopImagePosition = (cadPictBox.ClientRectangle.Height - visibleArea.Height) / 2f;
            cadPictBox.Invalidate();
        }

        public void LoadFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return;

            if (cadImage != null)
            {
                cadImage.Dispose();
                cadImage = null;
            }

            imageScale = 1f;
            imageScalePrev = 1f;
            position = new PointF();

            cadImage = CADImage.CreateImageByExtension(fileName);
            if (cadImage != null)
            {
                cadImage.LoadFromFile(fileName);
            }

            SetCADImageOptions();
            AfterLoaded?.Invoke(this, fileName);
        }

        public void SetCADImageOptions()
        {
            if (cadImage == null || cadPictBox == null) return;

            cadImage.IsShowLineWeight = false;

            SetBackColor(IsDark);

            if (cadPictBox.BackColor == Color.White)
            {
                cadImage.DefaultColor = Color.Black;
                cadImage.BackgroundColor = Color.White;
            }
            else
            {
                cadImage.DefaultColor = Color.White;
                cadImage.BackgroundColor = Color.Black;
            }

            SetDrawingColors(drawingColor);
            SetTextVisible(textVisible);

            Resize();
            SetPictureBoxPosition(position);
        }

        public bool SetDrawingColors(bool val)
        {
            drawingColor = val;
            if (cadImage == null) return false;
            cadImage.DrawMode = val ? CADDrawMode.Normal : CADDrawMode.Black;
            cadPictBox?.Invalidate();
            return val;
        }

        public bool SetBackColor(bool val)
        {
            IsDark = val;
            if (cadPictBox == null) return val;

            if (val)
            {
                cadPictBox.BackColor = Color.Black;
                if (cadImage != null)
                {
                    cadImage.DefaultColor = Color.White;
                    cadImage.BackgroundColor = Color.Black;
                    if (cadImage.Painter?.Settings != null)
                    {
                        cadImage.Painter.Settings.DefaultColor = Color.White.ToArgb();
                        cadImage.Painter.Settings.BackgroundColor = Color.Black.ToArgb();
                    }
                }
            }
            else
            {
                cadPictBox.BackColor = Color.White;
                if (cadImage != null)
                {
                    cadImage.DefaultColor = Color.Black;
                    cadImage.BackgroundColor = Color.White;
                    if (cadImage.Painter?.Settings != null)
                    {
                        cadImage.Painter.Settings.DefaultColor = Color.Black.ToArgb();
                        cadImage.Painter.Settings.BackgroundColor = Color.White.ToArgb();
                    }
                }
            }

            cadPictBox.Invalidate();
            return val;
        }

        public bool SetTextVisible(bool val)
        {
            textVisible = val;
            if (cadImage == null) return val;
            cadImage.TextVisible = val;
            cadPictBox?.Invalidate();
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
