using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
namespace QuickLook.Plugin.DwgsViewer.Core
{
    internal static class CadAutomationHelper
    {
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string lpszProgID, out Guid lpclsid);

        [DllImport("oleaut32.dll", PreserveSig = false)]
        private static extern void GetActiveObject(ref Guid rclsid, IntPtr pvReserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private static readonly string[] AcadProgIds = {
            "AutoCAD.Application",
            "AutoCAD.Application.24", // 2021-2026
            "AutoCAD.Application.23", // 2019-2020
            "AutoCAD.Application.22", // 2018
            "AutoCAD.Application.21", // 2017
            "AutoCAD.Application.20", // 2015-2016
            "AutoCAD.Application.19", // 2013-2014
            "AutoCAD.Application.18", // 2010-2012
            "AutoCAD.Application.17"  // 2007-2009
        };

        private static readonly string[] GcadProgIds = {
            "Gcad.Application",
            "GstarCAD.Application"
        };

        private static readonly string[] ZwcadProgIds = {
            "Zcad.Application",
            "ZwCAD.Application"
        };

        private static bool TryGetActiveObject(string progId, out object? obj)
        {
            obj = null;
            try
            {
                int hr = CLSIDFromProgID(progId, out Guid clsid);
                if (hr == 0)
                {
                    GetActiveObject(ref clsid, IntPtr.Zero, out obj);
                    return obj != null;
                }
            }
            catch
            {
                // Not running or failed
            }
            return false;
        }

        public static bool CheckRunningCad(out string cadName)
        {
            var app = GetActiveCadApplication(out cadName);
            return app != null;
        }

        public static object? GetActiveCadApplication(out string cadName)
        {
            // 1. Try AutoCAD
            foreach (var id in AcadProgIds)
            {
                if (TryGetActiveObject(id, out object? app))
                {
                    cadName = "AutoCAD";
                    return app;
                }
            }

            // 2. Try 浩辰CAD (GstarCAD)
            foreach (var id in GcadProgIds)
            {
                if (TryGetActiveObject(id, out object? app))
                {
                    cadName = "浩辰CAD (GstarCAD)";
                    return app;
                }
            }

            // 3. Try 中望CAD (ZWCAD)
            foreach (var id in ZwcadProgIds)
            {
                if (TryGetActiveObject(id, out object? app))
                {
                    cadName = "中望CAD (ZWCAD)";
                    return app;
                }
            }

            cadName = string.Empty;
            return null;
        }

        public static bool InsertDrawingIntoCad(string filePath, out string statusMessage)
        {
            if (!File.Exists(filePath))
            {
                statusMessage = "文件不存在";
                return false;
            }

            object? cadApp = GetActiveCadApplication(out string cadName);
            if (cadApp == null)
            {
                statusMessage = "未检测到运行中的 AutoCAD、浩辰CAD 或 中望CAD";
                return false;
            }

            try
            {
                Type appType = cadApp.GetType();

                // 尝试前置 CAD 窗口
                try
                {
                    appType.InvokeMember("Visible", BindingFlags.SetProperty, null, cadApp, new object[] { true });
                    object? hwndObj = appType.InvokeMember("HWND", BindingFlags.GetProperty, null, cadApp, null);
                    if (hwndObj is int hwndInt && hwndInt != 0)
                    {
                        SetForegroundWindow(new IntPtr(hwndInt));
                    }
                    else if (hwndObj is long hwndLong && hwndLong != 0)
                    {
                        SetForegroundWindow(new IntPtr(hwndLong));
                    }
                }
                catch { }

                object? activeDoc = appType.InvokeMember("ActiveDocument", BindingFlags.GetProperty, null, cadApp, null);
                if (activeDoc == null)
                {
                    statusMessage = $"{cadName} 未打开任何图纸";
                    return false;
                }

                // 统一格式化路径：Windows 路径反斜杠转正斜杠，避免转义错误
                string formattedPath = filePath.Replace('\\', '/');

                // 发送插入块命令
                // AutoCAD / 浩辰 / 中望 支持标准命令: _-INSERT "文件路径" (自动启动交互式定位)
                activeDoc.GetType().InvokeMember("SendCommand", BindingFlags.InvokeMethod, null, activeDoc, new object[] { $"_-INSERT \"{formattedPath}\" \n" });

                statusMessage = $"已发送至 {cadName}！请在 CAD 画布中点选插入位置";
                return true;
            }
            catch (Exception ex)
            {
                statusMessage = $"插入到 {cadName} 失败: {ex.Message}";
                return false;
            }
        }
    }
}
