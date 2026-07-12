using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using NUnit.Framework;
using UnityEditor;

namespace MCPForUnityTests.Editor.Helpers
{
    [TestFixture]
    public class HttpEndpointUtilityTests
    {
        private StringEditorPrefSnapshot _globalLocal;
        private StringEditorPrefSnapshot _projectLocal;
        private StringEditorPrefSnapshot _globalRemote;
        private StringEditorPrefSnapshot _projectRemote;

        [SetUp]
        public void SetUp()
        {
            _globalLocal = StringEditorPrefSnapshot.Capture(EditorPrefKeys.HttpBaseUrl);
            _projectLocal = StringEditorPrefSnapshot.Capture(
                HttpEndpointUtility.GetProjectScopedPrefKey(EditorPrefKeys.HttpBaseUrl));
            _globalRemote = StringEditorPrefSnapshot.Capture(EditorPrefKeys.HttpRemoteBaseUrl);
            _projectRemote = StringEditorPrefSnapshot.Capture(
                HttpEndpointUtility.GetProjectScopedPrefKey(EditorPrefKeys.HttpRemoteBaseUrl));

            EditorPrefs.DeleteKey(_projectLocal.Key);
            EditorPrefs.DeleteKey(_projectRemote.Key);
        }

        [TearDown]
        public void TearDown()
        {
            _globalLocal.Restore();
            _projectLocal.Restore();
            _globalRemote.Restore();
            _projectRemote.Restore();
        }

        [Test]
        public void GetLocalBaseUrl_UsesLegacyGlobalValue_WhenProjectOverrideIsMissing()
        {
            EditorPrefs.SetString(EditorPrefKeys.HttpBaseUrl, "http://localhost:8087/mcp/");

            Assert.AreEqual("http://127.0.0.1:8087", HttpEndpointUtility.GetLocalBaseUrl());
        }

        [Test]
        public void SaveLocalBaseUrl_WritesProjectOverride_WithoutChangingGlobalValue()
        {
            EditorPrefs.SetString(EditorPrefKeys.HttpBaseUrl, "http://127.0.0.1:8081");

            HttpEndpointUtility.SaveLocalBaseUrl("http://localhost:8090/mcp/");

            Assert.AreEqual("http://127.0.0.1:8081",
                EditorPrefs.GetString(EditorPrefKeys.HttpBaseUrl));
            Assert.AreEqual("http://127.0.0.1:8090",
                EditorPrefs.GetString(_projectLocal.Key));
            Assert.AreEqual("http://127.0.0.1:8090", HttpEndpointUtility.GetLocalBaseUrl());
        }

        [Test]
        public void SaveRemoteBaseUrl_WritesProjectOverride_WithoutChangingGlobalValue()
        {
            EditorPrefs.SetString(EditorPrefKeys.HttpRemoteBaseUrl, "https://global.example");

            HttpEndpointUtility.SaveRemoteBaseUrl("https://project.example/mcp/");

            Assert.AreEqual("https://global.example",
                EditorPrefs.GetString(EditorPrefKeys.HttpRemoteBaseUrl));
            Assert.AreEqual("https://project.example",
                EditorPrefs.GetString(_projectRemote.Key));
            Assert.AreEqual("https://project.example", HttpEndpointUtility.GetRemoteBaseUrl());
        }

        [Test]
        public void ClearOverrides_RestoresLegacyGlobalValues()
        {
            EditorPrefs.SetString(EditorPrefKeys.HttpBaseUrl, "http://127.0.0.1:8081");
            EditorPrefs.SetString(EditorPrefKeys.HttpRemoteBaseUrl, "https://global.example");
            HttpEndpointUtility.SaveLocalBaseUrl("http://127.0.0.1:8090");
            HttpEndpointUtility.SaveRemoteBaseUrl("https://project.example");

            HttpEndpointUtility.ClearLocalBaseUrlOverride();
            HttpEndpointUtility.ClearRemoteBaseUrlOverride();

            Assert.AreEqual("http://127.0.0.1:8081", HttpEndpointUtility.GetLocalBaseUrl());
            Assert.AreEqual("https://global.example", HttpEndpointUtility.GetRemoteBaseUrl());
        }

    }
}
