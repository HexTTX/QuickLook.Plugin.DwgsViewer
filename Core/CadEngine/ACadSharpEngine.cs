using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;
using Color = System.Drawing.Color;

namespace QuickLook.Plugin.DwgsViewer.Core.CadEngine
{
    /// <summary>
    /// 基于 ACadSharp (MIT 开源许可) 的纯原生 GDI+ 矢量渲染引擎。
    /// 【100% 纯净开源合规，零商业破解依赖】：
    /// 1. 原生解析 DWG (AutoCAD R13 ~ 2018) 及 DXF 几何图元
    /// 2. 递归展开图块引用（Insert / Block），支持平移、缩放与旋转变换矩阵
    /// 3. 精准数学求解 LwPolyline 凸度弧线（Bulge）、Arc、Circle、Ellipse
    /// 4. 支持单行文本（TextEntity）与多行文本（MText）中英文字体矢量排版
    /// 5. 自动适配深色/浅色底色与图层图元颜色
    /// </summary>
    public class ACadSharpEngine : ICadEngine
    {
        public string Name => "ACadSharp Open-Source Vector Engine";
        public bool IsAvailable => true;

        private readonly List<RenderSegment> _segments = new List<RenderSegment>();
        private readonly List<RenderText> _texts = new List<RenderText>();

        private double _minX, _minY, _maxX, _maxY;
        private bool _isLoaded;
        private bool _isDark = true;

        private struct RenderSegment
        {
            public XYZ P1;
            public XYZ P2;
            public Color Color;
        }

        private struct RenderText
        {
            public XYZ Location;
            public string Text;
            public double Height;
            public Color Color;
        }

        public Bitmap? RenderThumbnail(string filePath, int width, int height, bool isDark)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                var segments = new List<RenderSegment>();
                var texts = new List<RenderText>();
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;

                ParseDrawing(filePath, segments, texts, ref minX, ref minY, ref maxX, ref maxY);

                if (segments.Count == 0 && texts.Count == 0)
                    return null;

                double dw = maxX - minX;
                double dh = maxY - minY;
                if (dw <= 0) dw = 1;
                if (dh <= 0) dh = 1;

                double scale = Math.Min((width - 16) / dw, (height - 16) / dh);
                double offX = (width - dw * scale) / 2.0;
                double offY = (height - dh * scale) / 2.0;

                var bmp = new Bitmap(width, height);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(isDark ? Color.FromArgb(24, 24, 26) : Color.White);
                    g.SmoothingMode = SmoothingMode.AntiAlias;

                    // 绘制几何线段
                    foreach (var seg in segments)
                    {
                        float sx1 = (float)(offX + (seg.P1.X - minX) * scale);
                        float sy1 = (float)(height - offY - (seg.P1.Y - minY) * scale);
                        float sx2 = (float)(offX + (seg.P2.X - minX) * scale);
                        float sy2 = (float)(height - offY - (seg.P2.Y - minY) * scale);

                        Color penColor = AdjustColorForBackground(seg.Color, isDark);
                        using (var pen = new Pen(penColor, 1.0f))
                        {
                            g.DrawLine(pen, sx1, sy1, sx2, sy2);
                        }
                    }

                    // 绘制文字
                    foreach (var txt in texts)
                    {
                        float sx = (float)(offX + (txt.Location.X - minX) * scale);
                        float sy = (float)(height - offY - (txt.Location.Y - minY) * scale);
                        float pxHeight = (float)(txt.Height * scale);
                        if (pxHeight < 5) continue; // 缩略图下过小文字略过以提升速度

                        Color textColor = AdjustColorForBackground(txt.Color, isDark);
                        using (var font = new Font("Microsoft YaHei", pxHeight, GraphicsUnit.Pixel))
                        using (var brush = new SolidBrush(textColor))
                        {
                            g.DrawString(txt.Text, font, brush, sx, sy - pxHeight);
                        }
                    }
                }

                return bmp;
            }
            catch
            {
                return null;
            }
        }

        public bool Load(string filePath, bool isDark)
        {
            Unload();
            _isDark = isDark;

            try
            {
                _minX = double.MaxValue;
                _minY = double.MaxValue;
                _maxX = double.MinValue;
                _maxY = double.MinValue;

                ParseDrawing(filePath, _segments, _texts, ref _minX, ref _minY, ref _maxX, ref _maxY);

                _isLoaded = (_segments.Count > 0 || _texts.Count > 0);
                return _isLoaded;
            }
            catch
            {
                Unload();
                return false;
            }
        }

        public void RenderDetail(Graphics g, Rectangle clientRect, float zoom, float panX, float panY)
        {
            if (!_isLoaded || clientRect.Width <= 0 || clientRect.Height <= 0)
                return;

            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;

                double dw = _maxX - _minX;
                double dh = _maxY - _minY;
                if (dw <= 0) dw = 1;
                if (dh <= 0) dh = 1;

                int w = clientRect.Width;
                int h = clientRect.Height;

                // 基准居中缩放
                double baseScale = Math.Min((w - 40) / dw, (h - 40) / dh);
                double scale = baseScale * zoom;

                double centerViewX = w / 2.0 + panX;
                double centerViewY = h / 2.0 + panY;

                double cadCenterX = (_minX + _maxX) / 2.0;
                double cadCenterY = (_minY + _maxY) / 2.0;

                // 绘制几何线段
                foreach (var seg in _segments)
                {
                    float sx1 = (float)(centerViewX + (seg.P1.X - cadCenterX) * scale);
                    float sy1 = (float)(centerViewY - (seg.P1.Y - cadCenterY) * scale); // CAD Y轴朝上，屏幕朝下
                    float sx2 = (float)(centerViewX + (seg.P2.X - cadCenterX) * scale);
                    float sy2 = (float)(centerViewY - (seg.P2.Y - cadCenterY) * scale);

                    // 视口边界裁剪剔除
                    if ((sx1 < -50 && sx2 < -50) || (sx1 > w + 50 && sx2 > w + 50) ||
                        (sy1 < -50 && sy2 < -50) || (sy1 > h + 50 && sy2 > h + 50))
                        continue;

                    Color penColor = AdjustColorForBackground(seg.Color, _isDark);
                    using (var pen = new Pen(penColor, 1.2f))
                    {
                        g.DrawLine(pen, sx1, sy1, sx2, sy2);
                    }
                }

                // 绘制文字
                foreach (var txt in _texts)
                {
                    float sx = (float)(centerViewX + (txt.Location.X - cadCenterX) * scale);
                    float sy = (float)(centerViewY - (txt.Location.Y - cadCenterY) * scale);
                    float pxHeight = (float)(txt.Height * scale);

                    if (pxHeight < 4 || pxHeight > 2000) continue;
                    if (sx < -200 || sx > w + 200 || sy < -200 || sy > h + 200) continue;

                    Color textColor = AdjustColorForBackground(txt.Color, _isDark);
                    using (var font = new Font("Microsoft YaHei", pxHeight, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(textColor))
                    {
                        g.DrawString(txt.Text, font, brush, sx, sy - pxHeight);
                    }
                }
            }
            catch
            {
            }
        }

        public void SetBackColor(bool isDark)
        {
            _isDark = isDark;
        }

        public void Unload()
        {
            _segments.Clear();
            _texts.Clear();
            _isLoaded = false;
        }

        public void Dispose()
        {
            Unload();
        }

        // ─── 核心 DWG / DXF 实体解析器 ───

        private static void ParseDrawing(
            string filePath,
            List<RenderSegment> segments,
            List<RenderText> texts,
            ref double minX, ref double minY, ref double maxX, ref double maxY)
        {
            CadDocument doc;
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".dxf")
                doc = DxfReader.Read(filePath);
            else
                doc = DwgReader.Read(filePath);

            double localMinX = double.MaxValue;
            double localMinY = double.MaxValue;
            double localMaxX = double.MinValue;
            double localMaxY = double.MinValue;

            void UpdateBounds(XYZ p)
            {
                if (double.IsNaN(p.X) || double.IsInfinity(p.X)) return;
                if (double.IsNaN(p.Y) || double.IsInfinity(p.Y)) return;
                localMinX = Math.Min(localMinX, p.X);
                localMinY = Math.Min(localMinY, p.Y);
                localMaxX = Math.Max(localMaxX, p.X);
                localMaxY = Math.Max(localMaxY, p.Y);
            }

            void ExtractEntity(Entity ent, Matrix4 transform, Color parentColor)
            {
                Color color = parentColor;
                if (ent.Color.IsByLayer && ent.Layer != null)
                {
                    var c = ent.Layer.Color;
                    color = Color.FromArgb(c.R, c.G, c.B);
                }
                else if (!ent.Color.IsByLayer && !ent.Color.IsByBlock)
                {
                    var c = ent.Color;
                    color = Color.FromArgb(c.R, c.G, c.B);
                }

                if (ent is Line line)
                {
                    var p1 = transform * line.StartPoint;
                    var p2 = transform * line.EndPoint;
                    segments.Add(new RenderSegment { P1 = p1, P2 = p2, Color = color });
                    UpdateBounds(p1);
                    UpdateBounds(p2);
                }
                else if (ent is LwPolyline poly)
                {
                    for (int i = 0; i < poly.Vertices.Count; i++)
                    {
                        int nextIdx = (i + 1) % poly.Vertices.Count;
                        if (nextIdx == 0 && !poly.IsClosed) break;

                        var v1 = poly.Vertices[i];
                        var v2 = poly.Vertices[nextIdx];
                        double b = v1.Bulge;

                        if (Math.Abs(b) < 1e-4)
                        {
                            var p1 = transform * new XYZ(v1.Location.X, v1.Location.Y, 0);
                            var p2 = transform * new XYZ(v2.Location.X, v2.Location.Y, 0);
                            segments.Add(new RenderSegment { P1 = p1, P2 = p2, Color = color });
                            UpdateBounds(p1);
                            UpdateBounds(p2);
                        }
                        else
                        {
                            // 弧线凸度求解算法：bulge = tan(theta / 4)
                            double theta = 4.0 * Math.Atan(b);
                            double dx = v2.Location.X - v1.Location.X;
                            double dy = v2.Location.Y - v1.Location.Y;
                            double chord = Math.Sqrt(dx * dx + dy * dy);
                            if (chord < 1e-6) continue;

                            double radius = Math.Abs(chord / (2.0 * Math.Sin(theta / 2.0)));
                            double mx = (v1.Location.X + v2.Location.X) / 2.0;
                            double my = (v1.Location.Y + v2.Location.Y) / 2.0;
                            double nx = -dy / chord;
                            double ny = dx / chord;
                            double d = (chord / 2.0) / Math.Tan(theta / 2.0);
                            double cx = mx + nx * d;
                            double cy = my + ny * d;

                            double startAngle = Math.Atan2(v1.Location.Y - cy, v1.Location.X - cx);
                            int steps = 16;
                            double step = theta / steps;
                            XYZ prev = transform * new XYZ(v1.Location.X, v1.Location.Y, 0);
                            UpdateBounds(prev);
                            for (int k = 1; k <= steps; k++)
                            {
                                double ang = startAngle + k * step;
                                XYZ curr = transform * new XYZ(cx + radius * Math.Cos(ang), cy + radius * Math.Sin(ang), 0);
                                segments.Add(new RenderSegment { P1 = prev, P2 = curr, Color = color });
                                UpdateBounds(curr);
                                prev = curr;
                            }
                        }
                    }
                }
                else if (ent is Arc arc)
                {
                    int steps = 24;
                    double start = arc.StartAngle;
                    double end = arc.EndAngle;
                    if (end < start) end += Math.PI * 2;
                    double step = (end - start) / steps;
                    XYZ prev = transform * new XYZ(arc.Center.X + arc.Radius * Math.Cos(start), arc.Center.Y + arc.Radius * Math.Sin(start), 0);
                    UpdateBounds(prev);
                    for (int s = 1; s <= steps; s++)
                    {
                        double ang = start + s * step;
                        XYZ curr = transform * new XYZ(arc.Center.X + arc.Radius * Math.Cos(ang), arc.Center.Y + arc.Radius * Math.Sin(ang), 0);
                        segments.Add(new RenderSegment { P1 = prev, P2 = curr, Color = color });
                        UpdateBounds(curr);
                        prev = curr;
                    }
                }
                else if (ent is Circle circle)
                {
                    int steps = 36;
                    double step = Math.PI * 2 / steps;
                    XYZ prev = transform * new XYZ(circle.Center.X + circle.Radius, circle.Center.Y, 0);
                    UpdateBounds(prev);
                    for (int s = 1; s <= steps; s++)
                    {
                        double ang = s * step;
                        XYZ curr = transform * new XYZ(circle.Center.X + circle.Radius * Math.Cos(ang), circle.Center.Y + circle.Radius * Math.Sin(ang), 0);
                        segments.Add(new RenderSegment { P1 = prev, P2 = curr, Color = color });
                        UpdateBounds(curr);
                        prev = curr;
                    }
                }
                else if (ent is Ellipse ellipse)
                {
                    int steps = 36;
                    double step = Math.PI * 2 / steps;
                    double a = ellipse.MajorAxis;
                    double b = ellipse.MinorAxis;
                    double rot = Math.Atan2(ellipse.MajorAxisEndPoint.Y, ellipse.MajorAxisEndPoint.X);
                    XYZ prev = transform * new XYZ(ellipse.Center.X + a * Math.Cos(rot), ellipse.Center.Y + a * Math.Sin(rot), 0);
                    UpdateBounds(prev);
                    for (int s = 1; s <= steps; s++)
                    {
                        double ang = s * step;
                        double lx = a * Math.Cos(ang);
                        double ly = b * Math.Sin(ang);
                        double gx = ellipse.Center.X + (lx * Math.Cos(rot) - ly * Math.Sin(rot));
                        double gy = ellipse.Center.Y + (lx * Math.Sin(rot) + ly * Math.Cos(rot));
                        XYZ curr = transform * new XYZ(gx, gy, 0);
                        segments.Add(new RenderSegment { P1 = prev, P2 = curr, Color = color });
                        UpdateBounds(curr);
                        prev = curr;
                    }
                }
                else if (ent is TextEntity txt && !string.IsNullOrEmpty(txt.Value))
                {
                    var loc = transform * txt.InsertPoint;
                    texts.Add(new RenderText { Location = loc, Text = txt.Value, Height = txt.Height, Color = color });
                    UpdateBounds(loc);
                }
                else if (ent is MText mtxt && !string.IsNullOrEmpty(mtxt.Value))
                {
                    var loc = transform * mtxt.InsertPoint;
                    texts.Add(new RenderText { Location = loc, Text = mtxt.Value, Height = mtxt.Height, Color = color });
                    UpdateBounds(loc);
                }
                else if (ent is Insert ins && ins.Block != null)
                {
                    var t = Matrix4.CreateTranslation((double)ins.InsertPoint.X, (double)ins.InsertPoint.Y, 0);
                    var r = Matrix4.CreateRotationMatrix(0, 0, (double)ins.Rotation);
                    var s = Matrix4.CreateScalingMatrix((double)ins.XScale, (double)ins.YScale, 1.0);
                    var m = transform * t * r * s;

                    foreach (var bEnt in ins.Block.Entities)
                    {
                        ExtractEntity(bEnt, m, color);
                    }
                }
            }

            foreach (var ent in doc.Entities)
            {
                ExtractEntity(ent, Matrix4.Identity, Color.FromArgb(220, 220, 220));
            }

            minX = localMinX;
            minY = localMinY;
            maxX = localMaxX;
            maxY = localMaxY;
        }

        private static Color AdjustColorForBackground(Color c, bool isDark)
        {
            int brightness = (int)(c.R * 0.299 + c.G * 0.587 + c.B * 0.114);
            if (isDark)
            {
                // 暗色背景下：过暗线条提升为浅亮色，防止黑线看不见
                if (brightness < 45) return Color.FromArgb(220, 220, 220);
            }
            else
            {
                // 亮色背景下：过亮线条调暗，防止白线看不见
                if (brightness > 215) return Color.FromArgb(30, 30, 30);
            }
            return c;
        }
    }
}
