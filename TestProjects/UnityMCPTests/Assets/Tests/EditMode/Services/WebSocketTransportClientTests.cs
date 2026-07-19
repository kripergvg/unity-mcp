using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Services.Transport.Transports;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    [TestFixture]
    public class WebSocketTransportClientTests
    {
        private const string CandidateBuilderMethodName = "BuildConnectionCandidateUris";
        private const string SocketUnavailableReasonMethodName = "GetSocketUnavailableReason";
        private const string WebSocketTransportClientTypeName = "MCPForUnity.Editor.Services.Transport.Transports.WebSocketTransportClient";
        private static readonly MethodInfo BuildConnectionCandidateUrisMethod = ResolveCandidateBuilderMethod();
        private static readonly MethodInfo GetSocketUnavailableReasonMethod =
            typeof(WebSocketTransportClient).GetMethod(
                SocketUnavailableReasonMethodName,
                BindingFlags.NonPublic | BindingFlags.Static);

        [Test]
        public void BuildConnectionCandidateUris_NullEndpoint_ReturnsEmptyList()
        {
            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(null);

            // Assert
            Assert.IsNotNull(candidates);
            Assert.AreEqual(0, candidates.Count);
        }

        [Test]
        public void BuildConnectionCandidateUris_NonLocalhost_ReturnsOriginalOnly()
        {
            // Arrange
            var endpoint = new Uri("ws://127.0.0.1:8080/hub/plugin");

            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(endpoint);

            // Assert
            Assert.AreEqual(1, candidates.Count);
            Assert.AreEqual(endpoint, candidates[0]);
        }

        [Test]
        public void BuildConnectionCandidateUris_Localhost_AddsIPv4AndIPv6Fallbacks()
        {
            // Arrange
            var endpoint = new Uri("ws://localhost:8080/hub/plugin");

            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(endpoint);

            // Assert
            Assert.AreEqual(3, candidates.Count);
            CollectionAssert.AreEqual(
                new[] { "localhost", "127.0.0.1", "::1" },
                candidates.Select(uri => NormalizeHostForComparison(uri.Host)).ToArray());

            int uniqueCount = candidates
                .Select(uri => uri.AbsoluteUri)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            Assert.AreEqual(candidates.Count, uniqueCount, "Fallback list should not contain duplicate endpoints.");
        }

        [Test]
        public void BuildConnectionCandidateUris_LocalhostFallbacks_PreserveSchemePortPathAndQuery()
        {
            // Arrange
            var endpoint = new Uri("wss://localhost:9443/custom/path?mode=test");

            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(endpoint);

            // Assert
            Assert.AreEqual(3, candidates.Count);
            foreach (Uri candidate in candidates)
            {
                Assert.AreEqual("wss", candidate.Scheme);
                Assert.AreEqual(9443, candidate.Port);
                Assert.AreEqual("/custom/path", candidate.AbsolutePath);
                Assert.AreEqual("?mode=test", candidate.Query);
            }
        }

        [Test]
        public void GetSocketUnavailableReason_NonOpenSocket_ReportsState()
        {
            using var socket = new ClientWebSocket();

            string reason = InvokeGetSocketUnavailableReason(socket);

            StringAssert.Contains(WebSocketState.None.ToString(), reason);
        }

        [Test]
        public void GetSocketUnavailableReason_MissingSocket_ReportsUnavailable()
        {
            string reason = InvokeGetSocketUnavailableReason(null);

            StringAssert.Contains("unavailable", reason);
        }

        [Test]
        public void TransportManager_StateTracksUnexpectedClientDisconnect()
        {
            var client = new FakeTransportClient();
            var manager = new TransportManager();
            manager.Configure(() => client, () => new FakeTransportClient());

            Assert.IsTrue(manager.StartAsync(TransportMode.Http).GetAwaiter().GetResult());
            Assert.IsTrue(manager.IsRunning(TransportMode.Http));

            client.Disconnect("connection lost");

            Assert.IsFalse(manager.IsRunning(TransportMode.Http));
            Assert.IsFalse(manager.GetState(TransportMode.Http).IsConnected);
            Assert.AreEqual("connection lost", manager.GetState(TransportMode.Http).Error);

            manager.StopAsync(TransportMode.Http).GetAwaiter().GetResult();
        }

        private static List<Uri> InvokeBuildConnectionCandidateUris(Uri endpoint)
        {
            if (BuildConnectionCandidateUrisMethod == null)
            {
                Assert.Fail(BuildMissingMethodDiagnostic());
            }
            var result = BuildConnectionCandidateUrisMethod.Invoke(null, new object[] { endpoint });
            Assert.IsNotNull(result);
            Assert.IsInstanceOf<List<Uri>>(result);
            return (List<Uri>)result;
        }

        private static string InvokeGetSocketUnavailableReason(ClientWebSocket socket)
        {
            Assert.IsNotNull(
                GetSocketUnavailableReasonMethod,
                $"Expected private {SocketUnavailableReasonMethodName} method to exist.");
            return (string)GetSocketUnavailableReasonMethod.Invoke(null, new object[] { socket });
        }

        private static MethodInfo ResolveCandidateBuilderMethod()
        {
            MethodInfo direct = GetCandidateBuilderMethod(typeof(WebSocketTransportClient));
            if (direct != null)
            {
                return direct;
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidateType = assembly.GetType(WebSocketTransportClientTypeName);
                if (candidateType == null)
                {
                    continue;
                }

                MethodInfo method = GetCandidateBuilderMethod(candidateType);
                if (method != null)
                {
                    return method;
                }
            }

            return null;
        }

        private static MethodInfo GetCandidateBuilderMethod(Type type)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            MethodInfo direct = type.GetMethod(
                CandidateBuilderMethodName,
                flags,
                binder: null,
                types: new[] { typeof(Uri) },
                modifiers: null);
            if (direct != null)
            {
                return direct;
            }

            // Fallback for environments where signature binding can differ between loaded copies.
            return type.GetMethods(flags).FirstOrDefault(method =>
            {
                if (!string.Equals(method.Name, CandidateBuilderMethodName, StringComparison.Ordinal))
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(Uri);
            });
        }

        private static string BuildMissingMethodDiagnostic()
        {
            var sb = new StringBuilder();
            sb.Append("Expected private candidate builder method to exist. Searched loaded assemblies for ")
              .Append(WebSocketTransportClientTypeName)
              .Append('.')
              .Append(CandidateBuilderMethodName)
              .Append(". Loaded candidate types:");

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidateType = assembly.GetType(WebSocketTransportClientTypeName);
                if (candidateType == null)
                {
                    continue;
                }

                sb.Append("\n- ")
                  .Append(assembly.FullName)
                  .Append(" @ ")
                  .Append(string.IsNullOrEmpty(assembly.Location) ? "<dynamic>" : assembly.Location);
            }

            return sb.ToString();
        }

        private static string NormalizeHostForComparison(string host)
        {
            if (string.IsNullOrEmpty(host))
            {
                return host;
            }

            return host.Trim('[', ']');
        }

        private sealed class FakeTransportClient : IMcpTransportClient
        {
            public bool IsConnected { get; private set; }
            public string TransportName => "fake";
            public TransportState State { get; private set; } =
                TransportState.Disconnected("fake");

            public Task<bool> StartAsync()
            {
                IsConnected = true;
                State = TransportState.Connected(TransportName);
                return Task.FromResult(true);
            }

            public Task StopAsync()
            {
                Disconnect();
                return Task.CompletedTask;
            }

            public Task<bool> VerifyAsync()
            {
                return Task.FromResult(IsConnected);
            }

            public Task ReregisterToolsAsync()
            {
                return Task.CompletedTask;
            }

            public void Disconnect(string reason = null)
            {
                IsConnected = false;
                State = TransportState.Disconnected(TransportName, reason);
            }
        }
    }
}
