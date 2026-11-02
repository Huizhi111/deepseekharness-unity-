using System;
using System.Collections.Generic;
using System.IO;

namespace UnityAgentBridge
{
    /// <summary>Persistent bridge configuration (stored inside the project's Library folder).</summary>
    public class BridgeSettings
    {
        public int port = 17899;
        public bool autoStart = true;
        public bool allowRemoteCode = true;
        public bool verbose = false;
        public string token = "";

        public BridgeSettings Clone()
        {
            return new BridgeSettings
            {
                port = port,
                autoStart = autoStart,
                allowRemoteCode = allowRemoteCode,
                verbose = verbose,
                token = token
            };
        }
    }

    public static class BridgeSettingsStore
    {
        public static BridgeSettings Load()
        {
            BridgeSettings settings = null;
            try
            {
                if (File.Exists(BridgePaths.SettingsFile))
                {
                    var raw = J.AsDict(Json.Parse(File.ReadAllText(BridgePaths.SettingsFile)));
                    if (raw != null)
                    {
                        settings = new BridgeSettings
                        {
                            port = J.Int(raw, "port", 17899),
                            autoStart = J.Bool(raw, "autoStart", true),
                            allowRemoteCode = J.Bool(raw, "allowRemoteCode", true),
                            verbose = J.Bool(raw, "verbose", false),
                            token = J.Str(raw, "token", "")
                        };
                    }
                }
            }
            catch (Exception)
            {
                settings = null;
            }

            if (settings == null) settings = new BridgeSettings();
            if (settings.port <= 0 || settings.port > 65535) settings.port = 17899;
            if (string.IsNullOrEmpty(settings.token)) settings.token = NewToken();

            Save(settings);
            return settings;
        }

        public static void Save(BridgeSettings settings)
        {
            if (settings == null) return;
            BridgePaths.EnsureDirectories();

            var dict = new Dictionary<string, object>
            {
                { "port", settings.port },
                { "autoStart", settings.autoStart },
                { "allowRemoteCode", settings.allowRemoteCode },
                { "verbose", settings.verbose },
                { "token", settings.token }
            };

            try
            {
                File.WriteAllText(BridgePaths.SettingsFile, Json.SerializePretty(dict));
                File.WriteAllText(BridgePaths.TokenFile, settings.token);
            }
            catch (Exception)
            {
                // Settings persistence is best effort; the window reports failures on save.
            }
        }

        public static string NewToken()
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
