using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace QuickLook.Plugin.DwgsViewer.Core
{
    [DataContract]
    public class PluginConfigData
    {
        [DataMember]
        public List<string> Favorites { get; set; } = new List<string>();

        [DataMember]
        public Dictionary<string, int> FileInsertCounts { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        [DataMember]
        public Dictionary<string, int> FolderInsertCounts { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        [DataMember]
        public int Columns { get; set; } = 5;

        [DataMember]
        public bool DarkTheme { get; set; } = true;
    }

    public static class ConfigManager
    {
        private static readonly object _lock = new object();
        private static PluginConfigData _data = new PluginConfigData();
        private static string _configPath = string.Empty;

        static ConfigManager()
        {
            InitPath();
            Load();
        }

        private static void InitPath()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string pluginDir = Path.Combine(appData, @"pooi.moe\QuickLook\QuickLook.Plugin\QuickLook.Plugin.DwgsViewer");
                if (!Directory.Exists(pluginDir))
                {
                    Directory.CreateDirectory(pluginDir);
                }
                _configPath = Path.Combine(pluginDir, "config.json");
            }
            catch
            {
                // Fallback to local directory
                _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            }
        }

        public static int Columns
        {
            get => _data.Columns;
            set
            {
                _data.Columns = Math.Max(2, Math.Min(value, 12));
                Save();
            }
        }

        public static bool DarkTheme
        {
            get => _data.DarkTheme;
            set
            {
                _data.DarkTheme = value;
                Save();
            }
        }

        public static bool IsFavorite(string filePath)
        {
            lock (_lock)
            {
                string norm = NormalizePath(filePath);
                return _data.Favorites.Any(f => string.Equals(f, norm, StringComparison.OrdinalIgnoreCase));
            }
        }

        public static bool ToggleFavorite(string filePath)
        {
            lock (_lock)
            {
                string norm = NormalizePath(filePath);
                bool exists = _data.Favorites.Any(f => string.Equals(f, norm, StringComparison.OrdinalIgnoreCase));
                if (exists)
                {
                    _data.Favorites.RemoveAll(f => string.Equals(f, norm, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    _data.Favorites.Add(norm);
                }
                Save();
                return !exists;
            }
        }

        public static void RecordInsert(string filePath)
        {
            lock (_lock)
            {
                string normFile = NormalizePath(filePath);
                string? folder = Path.GetDirectoryName(filePath);
                string normFolder = folder != null ? NormalizePath(folder) : string.Empty;

                if (_data.FileInsertCounts.ContainsKey(normFile))
                    _data.FileInsertCounts[normFile]++;
                else
                    _data.FileInsertCounts[normFile] = 1;

                if (!string.IsNullOrEmpty(normFolder))
                {
                    if (_data.FolderInsertCounts.ContainsKey(normFolder))
                        _data.FolderInsertCounts[normFolder]++;
                    else
                        _data.FolderInsertCounts[normFolder] = 1;
                }

                Save();
            }
        }

        public static int GetInsertCount(string filePath)
        {
            lock (_lock)
            {
                string norm = NormalizePath(filePath);
                return _data.FileInsertCounts.TryGetValue(norm, out int c) ? c : 0;
            }
        }

        public static int GetFolderInsertCount(string folderPath)
        {
            lock (_lock)
            {
                string norm = NormalizePath(folderPath);
                return _data.FolderInsertCounts.TryGetValue(norm, out int c) ? c : 0;
            }
        }

        private static string NormalizePath(string p)
        {
            try
            {
                return Path.GetFullPath(p).TrimEnd('\\', '/');
            }
            catch
            {
                return p.TrimEnd('\\', '/');
            }
        }

        public static void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_configPath))
                    {
                        var serializer = new DataContractJsonSerializer(typeof(PluginConfigData));
                        using (var fs = new FileStream(_configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            var obj = serializer.ReadObject(fs) as PluginConfigData;
                            if (obj != null)
                            {
                                _data = obj;
                                if (_data.FileInsertCounts == null) _data.FileInsertCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                if (_data.FolderInsertCounts == null) _data.FolderInsertCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                if (_data.Favorites == null) _data.Favorites = new List<string>();
                            }
                        }
                    }
                }
                catch
                {
                    _data = new PluginConfigData();
                }
            }
        }

        public static void Save()
        {
            lock (_lock)
            {
                try
                {
                    var serializer = new DataContractJsonSerializer(typeof(PluginConfigData));
                    using (var fs = new FileStream(_configPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        serializer.WriteObject(fs, _data);
                    }
                }
                catch
                {
                    // Ignore save errors
                }
            }
        }
    }
}
