using System;
using System.IO;
using System.Linq;
using System.Windows;
using QuickLook.Common.Plugin;

namespace QuickLook.Plugin.DwgsViewer
{
    public class Plugin : IViewer
    {
        private static readonly string[] SupportedExtensions = { ".dwg", ".dxf" };
        private DwgsViewerControl? _control;

        public int Priority => 20; // 优先接管 .dwg 和 .dxf 格式

        public void Init()
        {
        }

        public bool CanHandle(string path)
        {
            if (string.IsNullOrEmpty(path) || Directory.Exists(path))
                return false;

            string ext = Path.GetExtension(path);
            if (!SupportedExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
                return false;

            return ValidateCadHeader(path, ext);
        }

        private static bool ValidateCadHeader(string path, string ext)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < 16)
                    return false;

                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new BinaryReader(fs))
                {
                    if (string.Equals(ext, ".dwg", StringComparison.OrdinalIgnoreCase))
                    {
                        // AutoCAD DWG 头魔数检测：前 4 字节必须为 "AC10" (如 AC1015, AC1021, AC1032 等)
                        byte[] versionBytes = reader.ReadBytes(4);
                        if (versionBytes.Length < 4) return false;
                        string ver = System.Text.Encoding.ASCII.GetString(versionBytes);
                        return ver.StartsWith("AC10");
                    }
                    else if (string.Equals(ext, ".dxf", StringComparison.OrdinalIgnoreCase))
                    {
                        // AutoCAD DXF 头格式检测：通常前几十字节包含 "SECTION" 或 "0\r\nSECTION"
                        byte[] headBytes = reader.ReadBytes(Math.Min(128, (int)fs.Length));
                        string headStr = System.Text.Encoding.ASCII.GetString(headBytes);
                        return headStr.IndexOf("SECTION", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               headStr.IndexOf("HEADER", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               headStr.IndexOf("0\r", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        public void Prepare(string path, ContextObject context)
        {
            context.SetPreferredSizeFit(new Size(980, 680), 0.85);
            context.CanResize = true;
            context.Title = Path.GetFileName(path);
        }

        public void View(string path, ContextObject context)
        {
            _control = new DwgsViewerControl(path, context);
            context.ViewerContent = _control;
            _control.LoadContent();
        }

        public void Cleanup()
        {
            _control?.Dispose();
            _control = null;
        }
    }
}
