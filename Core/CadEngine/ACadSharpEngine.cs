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
    /// 【极致性能优化版】：
    /// 1. 颜色预分组（Color-grouped batching）：大幅降低 GDI+ 状态切换开销
    /// 2. GDI+ 对象池化与缓存（Pen / SolidBrush / Font 零 GC 内存分配）
    /// 3. 精准数学求解 LwPolyline 凸度弧线（Bulge）、Arc、Circle、Ellipse
    /// 4. 支持单行文本（TextEntity）与多行文本（MText）中英文字体矢量排版
    /// 5. 自动适配深色/浅色底色反色保护与毫秒级视口边界剔除
    /// </summary>
    internal class ACadSharpEngine : ICadEngine
    {
        public string Name => "ACadSharp Open-Source Vector Engine";
        public bool IsAvailable => true;

        // 图元数据存储：按颜色归类分组，批次绘制
        private readonly Dictionary<Color, List<XYZPair>> _colorGroups = new Dictionary<Color, List<XYZPair>>();
        private readonly List<RenderText> _texts = new List<RenderText>();

        // GDI+ 对象缓存池，彻底避免缩放平移时的垃圾回收停顿与句柄泄漏
        private readonly Dictionary<Color, Pen> _penCache = new Dictionary<Color, Pen>();
        private readonly Dictionary<Color, SolidBrush> _brushCache = new Dictionary<Color, SolidBrush>();
        private readonly Dictionary<int, Font> _fontCache = new Dictionary<int, Font>();

        private double _minX, _minY, _maxX, _maxY;
        private bool _isLoaded;
        private bool _isDark = true;

        internal struct XYZPair
        {
            public XYZ P1;
            public XYZ P2;
            public XYZPair(XYZ p1, XYZ p2) { P1 = p1; P2 = p2; }
        }

        private struct RenderText
        {
            public XYZ Location;
            public string Text;
            public double Height;
            public Color Color;
            public StringAlignment Align;
            public StringAlignment LineAlign;
        }

        public Bitmap? RenderThumbnail(string filePath, int width, int height, bool isDark)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                var groups = new Dictionary<Color, List<XYZPair>>();
                var texts = new List<RenderText>();
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;

                ParseDrawing(filePath, groups, texts, ref minX, ref minY, ref maxX, ref maxY);

                if (groups.Count == 0 && texts.Count == 0)
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

                    // 按颜色批次绘制线段
                    foreach (var kvp in groups)
                    {
                        Color penColor = AdjustColorForBackground(kvp.Key, isDark);
                        using (var pen = new Pen(penColor, 1.0f))
                        {
                            foreach (var seg in kvp.Value)
                            {
                                float sx1 = (float)(offX + (seg.P1.X - minX) * scale);
                                float sy1 = (float)(height - offY - (seg.P1.Y - minY) * scale);
                                float sx2 = (float)(offX + (seg.P2.X - minX) * scale);
                                float sy2 = (float)(height - offY - (seg.P2.Y - minY) * scale);
                                g.DrawLine(pen, sx1, sy1, sx2, sy2);
                            }
                        }
                    }

                    // 绘制文字
                    foreach (var txt in texts)
                    {
                        float sx = (float)(offX + (txt.Location.X - minX) * scale);
                        float sy = (float)(height - offY - (txt.Location.Y - minY) * scale);
                        float pxHeight = (float)(txt.Height * scale);
                        if (pxHeight < 5) continue; // 缩略图过小文字掠过以保证极速生成

                        Color textColor = AdjustColorForBackground(txt.Color, isDark);
                        using (var font = new Font("Microsoft YaHei", pxHeight, GraphicsUnit.Pixel))
                        using (var brush = new SolidBrush(textColor))
                        using (var format = new StringFormat { Alignment = txt.Align, LineAlignment = txt.LineAlign })
                        {
                            g.DrawString(txt.Text, font, brush, sx, sy, format);
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

                ParseDrawing(filePath, _colorGroups, _texts, ref _minX, ref _minY, ref _maxX, ref _maxY);

                _isLoaded = (_colorGroups.Count > 0 || _texts.Count > 0);
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

                // 基准居中比例
                double baseScale = Math.Min((w - 40) / dw, (h - 40) / dh);
                double scale = baseScale * zoom;

                double centerViewX = w / 2.0 + panX;
                double centerViewY = h / 2.0 + panY;

                double cadCenterX = (_minX + _maxX) / 2.0;
                double cadCenterY = (_minY + _maxY) / 2.0;

                // 【性能极致优化】：利用缓存池与批次渲染，零 GC 产生！
                foreach (var kvp in _colorGroups)
                {
                    Color penColor = AdjustColorForBackground(kvp.Key, _isDark);
                    Pen pen = GetCachedPen(penColor);

                    var list = kvp.Value;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var seg = list[i];
                        float sx1 = (float)(centerViewX + (seg.P1.X - cadCenterX) * scale);
                        float sy1 = (float)(centerViewY - (seg.P1.Y - cadCenterY) * scale);
                        float sx2 = (float)(centerViewX + (seg.P2.X - cadCenterX) * scale);
                        float sy2 = (float)(centerViewY - (seg.P2.Y - cadCenterY) * scale);

                        // 快速粗粒度视口边界剔除
                        if ((sx1 < -100 && sx2 < -100) || (sx1 > w + 100 && sx2 > w + 100) ||
                            (sy1 < -100 && sy2 < -100) || (sy1 > h + 100 && sy2 > h + 100))
                            continue;

                        g.DrawLine(pen, sx1, sy1, sx2, sy2);
                    }
                }

                // 绘制文字
                for (int i = 0; i < _texts.Count; i++)
                {
                    var txt = _texts[i];
                    float sx = (float)(centerViewX + (txt.Location.X - cadCenterX) * scale);
                    float sy = (float)(centerViewY - (txt.Location.Y - cadCenterY) * scale);
                    float pxHeight = (float)(txt.Height * scale);

                    if (pxHeight < 4 || pxHeight > 2000) continue;
                    if (sx < -200 || sx > w + 200 || sy < -200 || sy > h + 200) continue;

                    Color textColor = AdjustColorForBackground(txt.Color, _isDark);
                    Font font = GetCachedFont((int)Math.Round(pxHeight));
                    Brush brush = GetCachedBrush(textColor);

                    using (var format = new StringFormat { Alignment = txt.Align, LineAlignment = txt.LineAlign })
                    {
                        g.DrawString(txt.Text, font, brush, sx, sy, format);
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
            _colorGroups.Clear();
            _texts.Clear();
            _isLoaded = false;

            // 清理缓存池
            foreach (var p in _penCache.Values) p.Dispose();
            _penCache.Clear();

            foreach (var b in _brushCache.Values) b.Dispose();
            _brushCache.Clear();

            foreach (var f in _fontCache.Values) f.Dispose();
            _fontCache.Clear();
        }

        public void Dispose()
        {
            Unload();
        }

        // ─── GDI+ 缓存池辅助函数 ───

        private Pen GetCachedPen(Color c)
        {
            if (!_penCache.TryGetValue(c, out var pen))
            {
                pen = new Pen(c, 1.2f);
                _penCache[c] = pen;
            }
            return pen;
        }

        private Brush GetCachedBrush(Color c)
        {
            if (!_brushCache.TryGetValue(c, out var brush))
            {
                brush = new SolidBrush(c);
                _brushCache[c] = brush;
            }
            return brush;
        }

        private Font GetCachedFont(int pxHeight)
        {
            pxHeight = Math.Max(6, Math.Min(256, pxHeight));
            if (!_fontCache.TryGetValue(pxHeight, out var font))
            {
                font = new Font("Microsoft YaHei", pxHeight, GraphicsUnit.Pixel);
                _fontCache[pxHeight] = font;
            }
            return font;
        }

        // ─── 核心 DWG / DXF 实体解析器 ───

        private static void ParseDrawing(
            string filePath,
            Dictionary<Color, List<XYZPair>> groups,
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

            void AddSegment(XYZ p1, XYZ p2, Color color)
            {
                if (!groups.TryGetValue(color, out var list))
                {
                    list = new List<XYZPair>();
                    groups[color] = list;
                }
                list.Add(new XYZPair(p1, p2));
                UpdateBounds(p1);
                UpdateBounds(p2);
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
                    AddSegment(transform * line.StartPoint, transform * line.EndPoint, color);
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
                            AddSegment(p1, p2, color);
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
                            for (int k = 1; k <= steps; k++)
                            {
                                double ang = startAngle + k * step;
                                XYZ curr = transform * new XYZ(cx + radius * Math.Cos(ang), cy + radius * Math.Sin(ang), 0);
                                AddSegment(prev, curr, color);
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
                    for (int s = 1; s <= steps; s++)
                    {
                        double ang = start + s * step;
                        XYZ curr = transform * new XYZ(arc.Center.X + arc.Radius * Math.Cos(ang), arc.Center.Y + arc.Radius * Math.Sin(ang), 0);
                        AddSegment(prev, curr, color);
                        prev = curr;
                    }
                }
                else if (ent is Circle circle)
                {
                    int steps = 36;
                    double step = Math.PI * 2 / steps;
                    XYZ prev = transform * new XYZ(circle.Center.X + circle.Radius, circle.Center.Y, 0);
                    for (int s = 1; s <= steps; s++)
                    {
                        double ang = s * step;
                        XYZ curr = transform * new XYZ(circle.Center.X + circle.Radius * Math.Cos(ang), circle.Center.Y + circle.Radius * Math.Sin(ang), 0);
                        AddSegment(prev, curr, color);
                        prev = curr;
                    }
                }
                else if (ent is Ellipse ellipse)
                {
                    XYZ vMajor = ellipse.MajorAxisEndPoint;
                    XYZ normal = ellipse.Normal;
                    XYZ vMinor = XYZ.Cross(normal, vMajor) * ellipse.RadiusRatio;

                    double start = ellipse.IsFullEllipse ? 0 : ellipse.StartParameter;
                    double end = ellipse.IsFullEllipse ? Math.PI * 2 : ellipse.EndParameter;
                    if (end < start) end += Math.PI * 2;

                    int steps = Math.Max(8, (int)Math.Ceiling(36 * (end - start) / (Math.PI * 2)));
                    double step = (end - start) / steps;

                    XYZ p0 = ellipse.Center + vMajor * Math.Cos(start) + vMinor * Math.Sin(start);
                    XYZ prev = transform * p0;

                    for (int s = 1; s <= steps; s++)
                    {
                        double ang = start + s * step;
                        XYZ p = ellipse.Center + vMajor * Math.Cos(ang) + vMinor * Math.Sin(ang);
                        XYZ curr = transform * p;
                        AddSegment(prev, curr, color);
                        prev = curr;
                    }
                }
                else if (ent is TextEntity txt && !string.IsNullOrEmpty(txt.Value))
                {
                    StringAlignment align = StringAlignment.Near;
                    if (txt.HorizontalAlignment == TextHorizontalAlignment.Center || txt.HorizontalAlignment == TextHorizontalAlignment.Middle)
                        align = StringAlignment.Center;
                    else if (txt.HorizontalAlignment == TextHorizontalAlignment.Right)
                        align = StringAlignment.Far;

                    StringAlignment lineAlign = StringAlignment.Far;
                    if (txt.VerticalAlignment == TextVerticalAlignmentType.Middle)
                        lineAlign = StringAlignment.Center;
                    else if (txt.VerticalAlignment == TextVerticalAlignmentType.Top)
                        lineAlign = StringAlignment.Near;

                    var pt = (txt.HorizontalAlignment != TextHorizontalAlignment.Left || txt.VerticalAlignment != TextVerticalAlignmentType.Baseline)
                        ? (txt.AlignmentPoint.X != 0 || txt.AlignmentPoint.Y != 0 ? txt.AlignmentPoint : txt.InsertPoint)
                        : txt.InsertPoint;

                    var loc = transform * pt;
                    texts.Add(new RenderText {
                        Location = loc,
                        Text = txt.Value,
                        Height = txt.Height,
                        Color = color,
                        Align = align,
                        LineAlign = lineAlign
                    });
                    UpdateBounds(loc);
                }
                else if (ent is MText mtxt && !string.IsNullOrEmpty(mtxt.Value))
                {
                    StringAlignment align = StringAlignment.Near;
                    StringAlignment lineAlign = StringAlignment.Near;

                    switch (mtxt.AttachmentPoint)
                    {
                        case AttachmentPointType.TopLeft:
                            align = StringAlignment.Near; lineAlign = StringAlignment.Near; break;
                        case AttachmentPointType.TopCenter:
                            align = StringAlignment.Center; lineAlign = StringAlignment.Near; break;
                        case AttachmentPointType.TopRight:
                            align = StringAlignment.Far; lineAlign = StringAlignment.Near; break;
                        case AttachmentPointType.MiddleLeft:
                            align = StringAlignment.Near; lineAlign = StringAlignment.Center; break;
                        case AttachmentPointType.MiddleCenter:
                            align = StringAlignment.Center; lineAlign = StringAlignment.Center; break;
                        case AttachmentPointType.MiddleRight:
                            align = StringAlignment.Far; lineAlign = StringAlignment.Center; break;
                        case AttachmentPointType.BottomLeft:
                            align = StringAlignment.Near; lineAlign = StringAlignment.Far; break;
                        case AttachmentPointType.BottomCenter:
                            align = StringAlignment.Center; lineAlign = StringAlignment.Far; break;
                        case AttachmentPointType.BottomRight:
                            align = StringAlignment.Far; lineAlign = StringAlignment.Far; break;
                    }

                    var loc = transform * mtxt.InsertPoint;
                    texts.Add(new RenderText {
                        Location = loc,
                        Text = mtxt.Value,
                        Height = mtxt.Height,
                        Color = color,
                        Align = align,
                        LineAlign = lineAlign
                    });
                    UpdateBounds(loc);
                }
                else if (ent is Solid solid)
                {
                    var c1 = transform * solid.FirstCorner;
                    var c2 = transform * solid.SecondCorner;
                    var c3 = transform * solid.ThirdCorner;
                    var c4 = transform * solid.FourthCorner;
                    AddSegment(c1, c2, color);
                    AddSegment(c2, c4, color);
                    AddSegment(c4, c3, color);
                    AddSegment(c3, c1, color);
                }
                else if (ent is Leader ldr)
                {
                    for (int k = 0; k < ldr.Vertices.Count - 1; k++)
                    {
                        AddSegment(transform * ldr.Vertices[k], transform * ldr.Vertices[k + 1], color);
                    }
                }
                else if (ent is Spline spline)
                {
                    var pts = (spline.FitPoints != null && spline.FitPoints.Count > 1) ? spline.FitPoints : spline.ControlPoints;
                    if (pts != null && pts.Count > 1)
                    {
                        for (int k = 0; k < pts.Count - 1; k++)
                        {
                            AddSegment(transform * pts[k], transform * pts[k + 1], color);
                        }
                    }
                }
                else if (ent is Polyline2D p2d)
                {
                    var vList = new List<XYZ>();
                    foreach (var v in p2d.Vertices) vList.Add(v.Location);
                    for (int k = 0; k < vList.Count - 1; k++)
                    {
                        AddSegment(transform * vList[k], transform * vList[k + 1], color);
                    }
                    if (p2d.IsClosed && vList.Count > 2)
                    {
                        AddSegment(transform * vList[vList.Count - 1], transform * vList[0], color);
                    }
                }
                else if (ent is Polyline3D p3d)
                {
                    var vList = new List<XYZ>();
                    foreach (var v in p3d.Vertices) vList.Add(v.Location);
                    for (int k = 0; k < vList.Count - 1; k++)
                    {
                        AddSegment(transform * vList[k], transform * vList[k + 1], color);
                    }
                    if (p3d.IsClosed && vList.Count > 2)
                    {
                        AddSegment(transform * vList[vList.Count - 1], transform * vList[0], color);
                    }
                }
                else if (ent is Dimension dim && dim.Block != null)
                {
                    // AutoCAD 尺寸标注（标注线、延伸线、箭头、测量数字全部内嵌在匿名图块 *D 中）
                    foreach (var bEnt in dim.Block.Entities)
                    {
                        if (bEnt is ACadSharp.Entities.Point && bEnt.Layer?.Name == "Defpoints")
                            continue;
                        ExtractEntity(bEnt, transform, color);
                    }
                }
                else if (ent is Hatch hatch)
                {
                    // 提取图案填充的外轮廓边界线（墙体、剖面等）
                    foreach (var path in hatch.Paths)
                    {
                        foreach (var edge in path.Edges)
                        {
                            if (edge is Hatch.BoundaryPath.Line hLine)
                            {
                                AddSegment(transform * new XYZ(hLine.Start.X, hLine.Start.Y, 0),
                                           transform * new XYZ(hLine.End.X, hLine.End.Y, 0), color);
                            }
                            else if (edge is Hatch.BoundaryPath.Arc hArc)
                            {
                                int steps = 16;
                                double start = hArc.StartAngle;
                                double end = hArc.EndAngle;
                                if (!hArc.CounterClockWise) { var tmp = start; start = end; end = tmp; }
                                if (end < start) end += Math.PI * 2;
                                double step = (end - start) / steps;
                                XYZ prev = transform * new XYZ(hArc.Center.X + hArc.Radius * Math.Cos(start), hArc.Center.Y + hArc.Radius * Math.Sin(start), 0);
                                for (int k = 1; k <= steps; k++)
                                {
                                    double ang = start + k * step;
                                    XYZ curr = transform * new XYZ(hArc.Center.X + hArc.Radius * Math.Cos(ang), hArc.Center.Y + hArc.Radius * Math.Sin(ang), 0);
                                    AddSegment(prev, curr, color);
                                    prev = curr;
                                }
                            }
                            else if (edge is Hatch.BoundaryPath.Polyline hPoly)
                            {
                                for (int k = 0; k < hPoly.Vertices.Count - 1; k++)
                                {
                                    AddSegment(transform * new XYZ(hPoly.Vertices[k].X, hPoly.Vertices[k].Y, 0),
                                               transform * new XYZ(hPoly.Vertices[k + 1].X, hPoly.Vertices[k + 1].Y, 0), color);
                                }
                                if (hPoly.IsClosed && hPoly.Vertices.Count > 2)
                                {
                                    AddSegment(transform * new XYZ(hPoly.Vertices[hPoly.Vertices.Count - 1].X, hPoly.Vertices[hPoly.Vertices.Count - 1].Y, 0),
                                               transform * new XYZ(hPoly.Vertices[0].X, hPoly.Vertices[0].Y, 0), color);
                                }
                            }
                        }
                    }
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
                if (brightness < 45) return Color.FromArgb(220, 220, 220);
            }
            else
            {
                if (brightness > 215) return Color.FromArgb(30, 30, 30);
            }
            return c;
        }
    }
}
