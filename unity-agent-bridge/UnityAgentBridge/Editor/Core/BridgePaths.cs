using System;
using System.IO;
using UnityEngine;

namespace UnityAgentBridge
{
    /// <summary>Well known locations used by the bridge.</summary>
    public static class BridgePaths
    {
        public const string GeneratedFolderAssetPath = "Assets/UnityAgentBridgeGenerated";

        public static string ProjectRoot
        {
            get
            {
                var parent = Directory.GetParent(Application.dataPath);
                return parent != null ? parent.FullName : Directory.GetCurrentDirectory();
            }
        }

        public static string LibraryDir
        {
            get { return Path.Combine(ProjectRoot, "Library", "UnityAgentBridge"); }
        }

        public static string JobsDir
        {
            get { return Path.Combine(LibraryDir, "jobs"); }
        }

        public static string SettingsFile
        {
            get { return Path.Combine(LibraryDir, "settings.json"); }
        }

        public static string TokenFile
        {
            get { return Path.Combine(LibraryDir, "token.txt"); }
        }

        public static string ServerLogFile
        {
            get { return Path.Combine(LibraryDir, "bridge.log"); }
        }

        public static string GeneratedDir
        {
            get
            {
                return Path.Combine(ProjectRoot, GeneratedFolderAssetPath.Replace('/', Path.DirectorySeparatorChar));
            }
        }

        public static void EnsureDirectories()
        {
            try
            {
                if (!Directory.Exists(LibraryDir)) Directory.CreateDirectory(LibraryDir);
                if (!Directory.Exists(JobsDir)) Directory.CreateDirectory(JobsDir);
                if (!Directory.Exists(GeneratedDir)) Directory.CreateDirectory(GeneratedDir);
            }
            catch (Exception)
            {
                // Directory creation failures surface later as clearer errors.
            }
        }
    }
}
