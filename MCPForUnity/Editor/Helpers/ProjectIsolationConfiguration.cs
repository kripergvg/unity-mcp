using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace MCPForUnity.Editor.Helpers
{
    /// <summary>
    /// Optional project-local configuration for a dedicated HTTP server.
    /// Presence of UserSettings/MCPForUnityProject.json enables isolated mode.
    /// </summary>
    public sealed class ProjectIsolationConfiguration
    {
        public const int CurrentSchemaVersion = 1;
        private const string ConfigFileName = "MCPForUnityProject.json";
        private static readonly object Sync = new();

        private static bool _loaded;
        private static ProjectIsolationConfiguration _current;
        private static Exception _loadException;
        private static string _configPathOverride;

        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; private set; }

        [JsonProperty("httpPort")]
        public int HttpPort { get; private set; }

        [JsonProperty("serverSource")]
        public string ServerSource { get; private set; }

        [JsonProperty("serverExecutable")]
        public string ServerExecutable { get; private set; }

        public string HttpBaseUrl => $"http://127.0.0.1:{HttpPort}";

        public static string ConfigPath
        {
            get
            {
                if (!string.IsNullOrEmpty(_configPathOverride))
                    return _configPathOverride;

                string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                return Path.Combine(projectRoot, "UserSettings", ConfigFileName);
            }
        }

        public static ProjectIsolationConfiguration Current
        {
            get
            {
                lock (Sync)
                {
                    if (_loaded)
                    {
                        if (_loadException != null)
                            throw new InvalidDataException(_loadException.Message, _loadException);
                        return _current;
                    }

                    _loaded = true;
                    try
                    {
                        _current = LoadFromPath(ConfigPath);
                        return _current;
                    }
                    catch (Exception ex)
                    {
                        _loadException = ex;
                        throw;
                    }
                }
            }
        }

        public static bool IsEnabled => Current != null;

        internal static ProjectIsolationConfiguration LoadFromPath(string path)
        {
            if (!File.Exists(path))
                return null;

            ProjectIsolationConfiguration config;
            try
            {
                config = JsonConvert.DeserializeObject<ProjectIsolationConfiguration>(
                    File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    $"Invalid MCP for Unity project configuration at '{path}': {ex.Message}",
                    ex);
            }

            if (config == null)
                throw new InvalidDataException(
                    $"MCP for Unity project configuration at '{path}' is empty.");

            if (config.SchemaVersion != CurrentSchemaVersion)
                throw new InvalidDataException(
                    $"Unsupported MCP for Unity project configuration schemaVersion " +
                    $"{config.SchemaVersion} at '{path}'. Expected {CurrentSchemaVersion}.");

            if (config.HttpPort < 1024 || config.HttpPort > 65535)
                throw new InvalidDataException(
                    $"Invalid httpPort {config.HttpPort} at '{path}'. " +
                    "Use a port between 1024 and 65535.");

            if (string.IsNullOrWhiteSpace(config.ServerSource))
                throw new InvalidDataException(
                    $"Missing serverSource at '{path}'. " +
                    "Use the pinned Python server source that matches the Unity package.");

            if (!string.IsNullOrWhiteSpace(config.ServerExecutable))
            {
                if (!Path.IsPathRooted(config.ServerExecutable))
                    throw new InvalidDataException(
                        $"serverExecutable must be an absolute path at '{path}'.");
                if (!File.Exists(config.ServerExecutable))
                    throw new InvalidDataException(
                        $"serverExecutable does not exist at '{config.ServerExecutable}'.");
            }

            return config;
        }

        internal static void SetConfigPathForTests(string path)
        {
            lock (Sync)
            {
                _configPathOverride = path;
                _current = null;
                _loadException = null;
                _loaded = false;
            }
        }

        internal static void ResetForTests()
        {
            lock (Sync)
            {
                _configPathOverride = null;
                _current = null;
                _loadException = null;
                _loaded = false;
            }
        }
    }
}
