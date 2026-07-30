using Maicon.SceneAnalyzer.Editor.Mcp;
using NUnit.Framework;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
    public sealed class CodeStudioMcpRegistrationTests
    {
        [Test]
        public void RuntimeCandidates_AreLoopbackOnlyAndDeduplicated()
        {
            var candidates = CodeStudioMcpRegistration.BuildRuntimeCandidates(
                "https://example.com/api",
                "http://127.0.0.1:7444/path",
                "http://127.0.0.1:7444/other");

            CollectionAssert.AreEqual(
                new[]
                {
                    "http://127.0.0.1:7444",
                    "http://127.0.0.1:7333",
                    "http://localhost:7333",
                },
                candidates);
        }

        [Test]
        public void RegistrationPayload_UsesCodeStudioUnityContract()
        {
            var mcpUrl = CodeStudioMcpRegistration.BuildUnityMcpUrl(
                McpTransportMode.Broker,
                8765,
                "ws://localhost:9876/bridge");
            var payload = CodeStudioMcpRegistration.BuildRegistrationPayload(mcpUrl, "secret");

            Assert.That(mcpUrl, Is.EqualTo("http://localhost:9876/mcp/"));
            Assert.That((string)payload["id"], Is.EqualTo("unity"));
            Assert.That((string)payload["name"], Is.EqualTo("unity-editor"));
            Assert.That((string)payload["transport"], Is.EqualTo("streamable_http"));
            Assert.That((bool?)payload["enabled"], Is.True);
            Assert.That((string)payload["url"], Is.EqualTo(mcpUrl));
            Assert.That((string)payload["auth_type"], Is.EqualTo("bearer"));
            Assert.That((string)payload["auth_token"], Is.EqualTo("secret"));
            Assert.That((string)payload["default_tier"], Is.EqualTo("unity"));
        }

        [TestCase(
            "ws://127.0.0.1:8765/bridge",
            "http://127.0.0.1:8765/mcp/",
            "http://127.0.0.1:8765/health")]
        [TestCase(
            "wss://localhost:9443/bridge",
            "https://localhost:9443/mcp/",
            "https://localhost:9443/health")]
        [TestCase(
            "ws://[::1]:8765/bridge",
            "http://[::1]:8765/mcp/",
            "http://[::1]:8765/health")]
        public void BrokerUrls_PreserveReachableAuthorityAndSecurity(
            string bridgeUrl,
            string expectedMcpUrl,
            string expectedHealthUrl)
        {
            var mcpUrl = CodeStudioMcpRegistration.BuildUnityMcpUrl(
                McpTransportMode.Broker,
                1234,
                bridgeUrl);

            Assert.That(mcpUrl, Is.EqualTo(expectedMcpUrl));
            Assert.That(
                CodeStudioMcpRegistration.BuildBrokerHealthUrl(mcpUrl),
                Is.EqualTo(expectedHealthUrl));
        }
    }
}
