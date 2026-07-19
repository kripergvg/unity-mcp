using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Windows;
using UnityEditor;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Ensures HTTP transports resume after domain reloads similar to the legacy stdio bridge.
    /// </summary>
    [InitializeOnLoad]
    internal static class HttpBridgeReloadHandler
    {
        private static readonly TimeSpan[] ResumeRetrySchedule =
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        };
        private static readonly TimeSpan ResumeRetryTailInterval = TimeSpan.FromSeconds(30);

        static HttpBridgeReloadHandler()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
        }

        private static void OnBeforeAssemblyReload()
        {
            try
            {
                var transport = MCPServiceLocator.TransportManager;
                bool useHttp = EditorConfigurationCache.Instance.UseHttpTransport;
                bool persistentConnectionRequested = IsPersistentConnectionRequested();
                bool shouldResume = useHttp
                    && (transport.IsRunning(TransportMode.Http)
                        || transport.IsRecovering(TransportMode.Http)
                        || persistentConnectionRequested);

                if (shouldResume)
                {
                    EditorPrefs.SetBool(GetResumePrefKey(), true);
                }
                else
                {
                    EditorPrefs.DeleteKey(GetResumePrefKey());
                }

                if (shouldResume)
                {
                    // beforeAssemblyReload is synchronous; force a synchronous teardown so we do not
                    // leave an orphaned socket due to an unfinished async close handshake.
                    transport.ForceStop(TransportMode.Http);
                }
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Failed to evaluate HTTP bridge reload state: {ex.Message}");
            }
        }

        private static void OnAfterAssemblyReload()
        {
            bool resume = false;
            try
            {
                // Only resume HTTP if it is still the selected transport.
                bool useHttp = EditorConfigurationCache.Instance.UseHttpTransport;
                string resumeKey = GetResumePrefKey();
                bool hasProjectScopedFlag = EditorPrefs.HasKey(resumeKey);
                bool storedResume = hasProjectScopedFlag
                    ? EditorPrefs.GetBool(resumeKey, false)
                    : !ProjectIsolationConfiguration.IsEnabled
                        && EditorPrefs.GetBool(EditorPrefKeys.ResumeHttpAfterReload, false);
                resume = useHttp && (storedResume || IsPersistentConnectionRequested());
                if (resume)
                {
                    EditorPrefs.DeleteKey(resumeKey);
                    if (!hasProjectScopedFlag)
                        EditorPrefs.DeleteKey(EditorPrefKeys.ResumeHttpAfterReload);
                }
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Failed to read HTTP bridge reload flag: {ex.Message}");
                resume = false;
            }

            if (!resume)
            {
                return;
            }

            // afterAssemblyReload already runs on the editor thread. Start immediately because
            // delayCall can remain dormant after a background domain reload until the window is focused.
            _ = ResumeHttpWithRetriesAsync();
        }

        private static async Task ResumeHttpWithRetriesAsync()
        {
            int attempt = 0;
            bool tailScheduleLogged = false;

            while (EditorConfigurationCache.Instance.UseHttpTransport)
            {
                var transport = MCPServiceLocator.TransportManager;
                if (transport.IsRunning(TransportMode.Http))
                {
                    MCPForUnityEditorWindow.RequestHealthVerification();
                    return;
                }

                if (transport.IsLifecycleBusy || transport.IsRecovering(TransportMode.Http))
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(1)); }
                    catch { return; }
                    continue;
                }

                int attemptNumber = attempt + 1;
                McpLog.Debug($"[HTTP Reload] Resume attempt {attemptNumber}");

                TimeSpan delay;
                if (attempt < ResumeRetrySchedule.Length)
                {
                    delay = ResumeRetrySchedule[attempt];
                }
                else
                {
                    if (!IsPersistentConnectionRequested())
                    {
                        McpLog.Warn("Failed to resume HTTP MCP bridge after domain reload");
                        return;
                    }

                    delay = ResumeRetryTailInterval;
                    if (!tailScheduleLogged)
                    {
                        tailScheduleLogged = true;
                        McpLog.Warn(
                            $"[HTTP Reload] Initial resume schedule exhausted. " +
                            $"Retrying every {ResumeRetryTailInterval.TotalSeconds}s.");
                    }
                }

                if (delay > TimeSpan.Zero)
                {
                    McpLog.Debug($"[HTTP Reload] Waiting {delay.TotalSeconds:0.#}s before resume attempt {attemptNumber}");
                    try { await Task.Delay(delay); }
                    catch { return; }
                }

                // Abort retries if the user switched transports while we were waiting.
                if (!EditorConfigurationCache.Instance.UseHttpTransport)
                {
                    return;
                }

                try
                {
                    bool started = await transport.StartAsync(TransportMode.Http);
                    if (started)
                    {
                        McpLog.Debug($"[HTTP Reload] Resume succeeded on attempt {attemptNumber}");
                        MCPForUnityEditorWindow.RequestHealthVerification();
                        return;
                    }

                    var state = MCPServiceLocator.TransportManager.GetState(TransportMode.Http);
                    string reason = string.IsNullOrWhiteSpace(state?.Error) ? "no error detail" : state.Error;
                    McpLog.Debug($"[HTTP Reload] Resume attempt {attemptNumber} failed: {reason}");
                }
                catch (Exception ex)
                {
                    McpLog.Debug($"[HTTP Reload] Resume attempt {attemptNumber} threw: {ex.Message}");
                }

                attempt++;
            }
        }

        private static string GetResumePrefKey()
        {
            return $"{EditorPrefKeys.ResumeHttpAfterReload}_{ProjectIdentityUtility.GetProjectHash()}";
        }

        private static bool IsPersistentConnectionRequested()
        {
            return ProjectIsolationConfiguration.IsEnabled
                || EditorPrefs.GetBool(EditorPrefKeys.AutoStartOnLoad, false);
        }
    }
}
