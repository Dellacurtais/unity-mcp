using Maicon.SceneAnalyzer.Editor.Mcp;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
    public sealed class ExternalMcpClientInstallerTests
    {
        private const string Endpoint = "http://127.0.0.1:8765/mcp/";

        [Test]
        public void ChatGptUpsert_ReplacesTransportAndAuthWithoutTouchingOtherServers()
        {
            var original =
                "model = \"gpt-test\"\n\n" +
                "[mcp_servers.unity]\n" +
                "command = \"old-unity\"\n" +
                "args = [\n" +
                "  \"--legacy\",\n" +
                "]\n" +
                "tool_timeout_sec = 300\n\n" +
                "[mcp_servers.unity.env]\n" +
                "OLD_TOKEN = \"secret\"\n\n" +
                "[mcp_servers.unity.tools.write]\n" +
                "approval_mode = \"prompt\"\n\n" +
                "[mcp_servers.docs]\n" +
                "url = \"https://example.test/mcp\"\n";

            var updated = ExternalMcpClientInstaller.UpsertChatGptConfig(original, Endpoint);

            StringAssert.Contains("model = \"gpt-test\"", updated);
            StringAssert.Contains("[mcp_servers.unity]", updated);
            StringAssert.Contains("url = \"" + Endpoint + "\"", updated);
            StringAssert.Contains(
                "bearer_token_env_var = \"UNITY_MCP_BROKER_TOKEN\"", updated);
            StringAssert.Contains("enabled = true", updated);
            StringAssert.Contains("tool_timeout_sec = 300", updated);
            StringAssert.Contains("[mcp_servers.unity.tools.write]", updated);
            StringAssert.Contains("[mcp_servers.docs]", updated);
            StringAssert.DoesNotContain("command = \"old-unity\"", updated);
            StringAssert.DoesNotContain("--legacy", updated);
            StringAssert.DoesNotContain("[mcp_servers.unity.env]", updated);
            Assert.That(
                ExternalMcpClientInstaller.IsChatGptConfigCurrent(updated, Endpoint),
                Is.True);
            Assert.That(
                ExternalMcpClientInstaller.UpsertChatGptConfig(updated, Endpoint),
                Is.EqualTo(updated));
        }

        [Test]
        public void ChatGptUpsert_AppendsServerToExistingConfiguration()
        {
            var updated = ExternalMcpClientInstaller.UpsertChatGptConfig(
                "model = \"gpt-test\"\n",
                Endpoint);

            StringAssert.StartsWith("model = \"gpt-test\"", updated);
            StringAssert.Contains("[mcp_servers.unity]", updated);
            Assert.That(
                ExternalMcpClientInstaller.IsChatGptConfigCurrent(updated, Endpoint),
                Is.True);
        }

        [Test]
        public void ClaudeUpsert_UpdatesExistingUnityMcpEntryAndKeepsOtherData()
        {
            var root = JObject.Parse(@"{
              'theme': 'dark',
              'mcpServers': {
                'unity-mcp': {
                  'type': 'stdio',
                  'command': 'old-unity',
                  'args': ['--legacy'],
                  'timeout': 600000,
                  'headers': { 'X-Custom': 'keep' }
                },
                'docs': { 'type': 'http', 'url': 'https://example.test/mcp' }
              }
            }");

            ExternalMcpClientInstaller.UpsertClaudeConfig(root, Endpoint);

            var server = (JObject)root["mcpServers"]["unity-mcp"];
            Assert.That((string)root["theme"], Is.EqualTo("dark"));
            Assert.That((string)server["type"], Is.EqualTo("http"));
            Assert.That((string)server["url"], Is.EqualTo(Endpoint));
            Assert.That((string)server["headers"]["X-Custom"], Is.EqualTo("keep"));
            Assert.That(
                (string)server["headers"]["Authorization"],
                Is.EqualTo("Bearer ${UNITY_MCP_BROKER_TOKEN}"));
            Assert.That(server["command"], Is.Null);
            Assert.That(server["args"], Is.Null);
            Assert.That((int)server["timeout"], Is.EqualTo(600000));
            Assert.That(root["mcpServers"]["docs"], Is.Not.Null);
            Assert.That(root["mcpServers"]["unity"], Is.Null);
            Assert.That(
                ExternalMcpClientInstaller.IsClaudeConfigCurrent(server, Endpoint),
                Is.True);
        }

        [Test]
        public void ClaudeUpsert_ReusesLegacyUnityNameInsteadOfDuplicatingIt()
        {
            var root = JObject.Parse(@"{
              'mcpServers': {
                'unity': { 'type': 'http', 'url': 'http://localhost:1/mcp' }
              }
            }");

            ExternalMcpClientInstaller.UpsertClaudeConfig(root, Endpoint);

            Assert.That(root["mcpServers"]["unity"], Is.Not.Null);
            Assert.That(root["mcpServers"]["unity-mcp"], Is.Null);
        }
    }
}
