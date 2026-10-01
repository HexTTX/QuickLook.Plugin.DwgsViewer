using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using CADImport;

namespace QuickLook.Plugin.DwgsViewer.Core
{
    public static class DwgThumbnailExtractor
    {
        private static readonly byte[] Sentinel = {
            0x1F, 0x25, 0x6D, 0x07, 0xD4, 0x36, 0x28, 0x28,
            0x9D, 0x57, 0xCA, 0x3F, 0x9D, 0x44, 0x10, 0x2B
        };

        static DwgThumbnailExtractor()
        {
            UnlockWatermark();
        }

        public static void UnlockWatermark()
        {
            try
            {
                var asm = typeof(CADImport.CADImage).Assembly;
                var field = asm.ManifestModule.ResolveField(0x040005B7);
                field?.SetValue(null, 1);
            }
            catch { }
        }

        public static Bitmap? RenderCadDrawing(string filePath, int width, int height, bool isDark)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            // 1. 优先使用 CADImport 完整矢量引擎渲染（与 QuickLook.Plugin.CADImport 一致，完整呈现图元、线型与尺寸）
            try
            {
                var cadImage = CADImage.CreateImageByExtension(filePath);
                if (cadImage != null)
                {
                    cadImage.LoadFromFile(filePath);

                    cadImage.BackgroundColor = isDark ? Color.Black : Color.White;
                    cadImage.DefaultColor = isDark ? Color.White : Color.Black;
                    if (cadImage.Painter != null && cadImage.Painter.Settings != null)
                    {
                        cadImage.Painter.Settings.BackgroundColor = (isDark ? Color.Black : Color.White).ToArgb();
                        cadImage.Painter.Settings.DefaultColor = (isDark ? Color.White : Color.Black).ToArgb();
                    }

                    double imgW = cadImage.AbsWidth;
                    double imgH = cadImage.AbsHeight;
                    if (imgW <= 0) imgW = 100;
                    if (imgH <= 0) imgH = 100;

                    Bitmap bmp = new Bitmap(width, height);
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(isDark ? Color.FromArgb(24, 24, 26) : Color.White);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                        float wh = (float)(imgW / imgH);
                        float new_wh = (float)width / height;
                        SizeF visibleArea;
                        if (new_wh > wh)
                            visibleArea = new SizeF(height * wh, height);
                        else
                            visibleArea = new SizeF(width, width / wh);

                        float left = (width - visibleArea.Width) / 2f;
                        float top = (height - visibleArea.Height) / 2f;
                        RectangleF rect = new RectangleF(left, top, visibleArea.Width, visibleArea.Height);

                        cadImage.Draw(g, rect);
                    }
                    cadImage.Dispose();
                    return bmp;
                }
            }
            catch
            {
                // 降级使用文件头内嵌图
            }

            // 2. 备用引擎：DWG 二进制文件头内嵌位图 / Windows Shell
            return ExtractThumbnail(filePath, width);
        }

        public static Bitmap? ExtractThumbnail(string filePath, int targetSize = 256)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".dwg")
            {
                var bmp = ExtractDwgHeaderThumbnail(filePath);
                if (bmp != null) return bmp;
            }

            // Fallback to Windows Shell Thumbnail (works for both .dwg and .dxf)
            return ExtractShellThumbnail(filePath, targetSize);
        }

        private static Bitmap? ExtractDwgHeaderThumbnail(string filePath)
        {
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new BinaryReader(fs))
                {
                    if (fs.Length < 128) return null;

                    // Verify DWG header starts with "AC10"
                    byte[] versionBytes = reader.ReadBytes(6);
                    string versionStr = System.Text.Encoding.ASCII.GetString(versionBytes);
                    if (!versionStr.StartsWith("AC10")) return null;

                    fs.Seek(0x0D, SeekOrigin.Begin);
                    uint thumbAddr = reader.ReadUInt32();
                    if (thumbAddr == 0 || thumbAddr + 32 >= fs.Length) return null;

                    fs.Seek(thumbAddr, SeekOrigin.Begin);
                    byte[] fileSentinel = reader.ReadBytes(16);
                    if (fileSentinel.Length < 16) return null;

                    for (int i = 0; i < 16; i++)
                    {
                        if (fileSentinel[i] != Sentinel[i]) return null;
                    }

                    uint dataSize = reader.ReadUInt32();
                    byte numImages = reader.ReadByte();

                    for (int i = 0; i < numImages; i++)
                    {
                        byte imgType = reader.ReadByte();
                        uint dataStart = reader.ReadUInt32();
                        uint dataLen = reader.ReadUInt32();

                        if (dataLen > 40 && dataStart + dataLen <= fs.Length)
                        {
                            long returnPos = fs.Position;
                            fs.Seek(dataStart, SeekOrigin.Begin);
                            byte[] rawBytes = reader.ReadBytes((int)dataLen);
                            fs.Seek(returnPos, SeekOrigin.Begin);

                            // 1. Direct BMP file ('BM' magic)
                            if (rawBytes.Length >= 2 && rawBytes[0] == 0x42 && rawBytes[1] == 0x4D)
                            {
                                using (var ms = new MemoryStream(rawBytes))
                                {
                                    return new Bitmap(ms);
                                }
                            }

                            // 2. Direct PNG file ('\x89PNG' magic)
                            if (rawBytes.Length >= 8 && rawBytes[0] == 0x89 && rawBytes[1] == 0x50 && rawBytes[2] == 0x4E && rawBytes[3] == 0x47)
                            {
                                using (var ms = new MemoryStream(rawBytes))
                                {
                                    return new Bitmap(ms);
                                }
                            }

                            // 3. BITMAPINFOHEADER (size 40) without BITMAPFILEHEADER (size 14)
                            if (rawBytes.Length >= 40)
                            {
                                int biSize = BitConverter.ToInt32(rawBytes, 0);
                                if (biSize == 40)
                                {
                                    ushort bpp = BitConverter.ToUInt16(rawBytes, 14);
                                    int colors = (bpp <= 8) ? (1 << bpp) : 0;
                                    int offsetToBits = 14 + 40 + (colors * 4);
                                    int totalFileSize = 14 + rawBytes.Length;

                                    byte[] fullBmp = new byte[totalFileSize];
                                    fullBmp[0] = 0x42; // 'B'
                                    fullBmp[1] = 0x4D; // 'M'
                                    Array.Copy(BitConverter.GetBytes(totalFileSize), 0, fullBmp, 2, 4);
                                    Array.Copy(BitConverter.GetBytes(offsetToBits), 0, fullBmp, 10, 4);
                                    Array.Copy(rawBytes, 0, fullBmp, 14, rawBytes.Length);

                                    using (var ms = new MemoryStream(fullBmp))
                                    {
                                        return new Bitmap(ms);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fallback to Shell thumbnail
            }

            return null;
        }

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage([In, MarshalAs(UnmanagedType.Struct)] SIZE size, [In] int flags, [Out] out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
            public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [Out, MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        public static Bitmap? ExtractShellThumbnail(string path, int size)
        {
            try
            {
                Guid guid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, guid, out IShellItemImageFactory factory);
                if (hr == 0 && factory != null)
                {
                    IntPtr hBitmap;
                    // SIIGBF_BIGGERSIZEOK = 0x01
                    int res = factory.GetImage(new SIZE(size, size), 0x01, out hBitmap);
                    if (res == 0 && hBitmap != IntPtr.Zero)
                    {
                        try
                        {
                            using (Bitmap bmp = Image.FromHbitmap(hBitmap))
                            {
                                return new Bitmap(bmp); // Cloned detached bitmap
                            }
                        }
                        finally
                        {
                            DeleteObject(hBitmap);
                        }
                    }
                }
            }
            catch
            {
                // Ignored
            }

            return null;
        }
    }
}
