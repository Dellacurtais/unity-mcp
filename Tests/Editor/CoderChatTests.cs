using System.Collections.Generic;
using Maicon.SceneAnalyzer.Editor.Chat;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Maicon.SceneAnalyzer.Editor.Tests
{
    public sealed class CoderChatTests
    {
        [Test]
        public void StreamEvents_CoalesceAdjacentTextWithoutCrossingBoundaries()
        {
            var toolEvent = CoderStreamEvent.Create("tool_start", "{\"id\":\"tool-1\"}", "3");
            var events = CoderStreamEvent.CoalesceAdjacentText(new[]
            {
                CoderStreamEvent.Create("text", "{\"text\":\"Olá \"}", "1"),
                CoderStreamEvent.Create("text", "{\"text\":\"mundo\"}", "2"),
                toolEvent,
                CoderStreamEvent.Create("text", "!", "4"),
            });

            Assert.That(events, Has.Count.EqualTo(3));
            Assert.That(events[0].TextDelta, Is.EqualTo("Olá mundo"));
            Assert.That(events[0].LastEventId, Is.EqualTo("2"));
            Assert.That(events[1], Is.SameAs(toolEvent));
            Assert.That(events[2].TextDelta, Is.EqualTo("!"));
            Assert.That(CoderStreamEvent.Create("done", "{}", null).IsTerminal, Is.True);
            Assert.That(CoderStreamEvent.Create("message_complete", "{}", null).IsTerminal, Is.True);
            Assert.That(CoderStreamEvent.Create("error", "{}", null).IsTerminal, Is.True);
            Assert.That(CoderStreamEvent.Create("text", "{}", null).IsTerminal, Is.False);
        }

        [Test]
        public void SessionBatch_NotifiesOnceForMultipleMutations()
        {
            var session = new ChatSession();
            var notifications = 0;
            session.OnChanged += () => notifications++;

            session.ApplyBatch(() =>
            {
                session.AddSystemNote("primeira");
                session.AddSystemNote("segunda");
                session.SetPendingPlan(new PendingPlan { Id = "plan-1", Plan = "Plano" });
            });

            Assert.That(notifications, Is.EqualTo(1));
            Assert.That(session.Messages, Has.Count.EqualTo(2));
            Assert.That(session.ConnectionState, Is.EqualTo(ChatConnectionState.WaitingForApproval));
        }

        [Test]
        public void PendingInteractions_UpsertAndRetryStateRemainDeterministic()
        {
            var session = new ChatSession();
            session.SetPendingQuestion(new PendingQuestion
            {
                Id = "question-1",
                Question = "Escolha",
                Options = new List<string> { "A", "B" },
            });
            session.SetPendingQuestion(new PendingQuestion
            {
                Id = "question-1",
                Question = "Escolha atualizada",
                Options = new List<string> { "C" },
            });

            Assert.That(session.Interactions, Has.Count.EqualTo(1));
            Assert.That(session.CurrentQuestion.Question, Is.EqualTo("Escolha atualizada"));
            Assert.That(session.CurrentQuestion.Choices, Has.Count.EqualTo(1));

            session.SetInteractionSubmitting("question:question-1");
            Assert.That(session.CurrentQuestion.State, Is.EqualTo(PendingInteractionState.Submitting));
            session.SetInteractionFailed("question:question-1", "falhou");
            Assert.That(session.CurrentQuestion.State, Is.EqualTo(PendingInteractionState.Failed));
            Assert.That(session.CurrentQuestion.Error, Is.EqualTo("falhou"));
            session.CompleteInteraction("question:question-1");

            Assert.That(session.Interactions, Is.Empty);
            Assert.That(session.ConnectionState, Is.EqualTo(ChatConnectionState.Idle));
        }

        [Test]
        public void SessionSnapshot_ReconcilesOptimisticMessageAndRestoresUnityReferences()
        {
            var context = new JObject
            {
                ["type"] = "unity-context",
                ["version"] = 1,
                ["references"] = new JArray
                {
                    new JObject
                    {
                        ["label"] = "Player",
                        ["isFolder"] = false,
                        ["target"] = new JObject
                        {
                            ["kind"] = "gameObject",
                            ["globalObjectId"] = "GlobalObjectId_V1-test",
                        },
                    },
                },
            };
            var content = "[unity-context — auto-injected by the Unity Scene Analyzer plugin test]\n\n---\n\n"
                + "Mova o objeto\n\n\u0060\u0060\u0060unity-context\n"
                + context.ToString(Formatting.None)
                + "\n\u0060\u0060\u0060";
            var snapshot = CoderSessionSnapshot.FromJson(new JObject
            {
                ["session"] = new JObject { ["id"] = 42, ["status"] = "idle" },
                ["message_rows"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = 7,
                        ["session_id"] = 42,
                        ["role"] = "user",
                        ["content_json"] = new JArray
                        {
                            new JObject { ["type"] = "text", ["text"] = content },
                        }.ToString(Formatting.None),
                    },
                },
            });

            Assert.That(snapshot.Messages, Has.Count.EqualTo(1));
            Assert.That(snapshot.Messages[0].Text, Is.EqualTo("Mova o objeto"));
            Assert.That(snapshot.Messages[0].References, Has.Count.EqualTo(1));
            Assert.That(snapshot.Messages[0].References[0].Label, Is.EqualTo("Player"));

            var session = new ChatSession();
            session.AddUser("Mova o objeto");
            session.MergeRemoteSnapshot(snapshot);

            Assert.That(session.Messages, Has.Count.EqualTo(1));
            Assert.That(session.Messages[0].IsRemote, Is.True);
            Assert.That(session.Messages[0].RemoteId, Is.EqualTo(7));
            Assert.That(session.Messages[0].References, Has.Count.EqualTo(1));
        }

        [Test]
        public void SessionSnapshot_CorrelatesToolResultsWithAssistantCalls()
        {
            var snapshot = CoderSessionSnapshot.FromJson(new JObject
            {
                ["session"] = new JObject { ["id"] = 9 },
                ["message_rows"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = 1,
                        ["session_id"] = 9,
                        ["role"] = "assistant",
                        ["content_json"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "tool_use",
                                ["id"] = "tool-1",
                                ["name"] = "read_scene",
                                ["input"] = new JObject { ["depth"] = 2 },
                            },
                        }.ToString(Formatting.None),
                    },
                    new JObject
                    {
                        ["id"] = 2,
                        ["session_id"] = 9,
                        ["role"] = "tool",
                        ["content_json"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "tool_result",
                                ["tool_use_id"] = "tool-1",
                                ["content"] = "ok",
                            },
                        }.ToString(Formatting.None),
                    },
                },
            });

            Assert.That(snapshot.Messages, Has.Count.EqualTo(1));
            Assert.That(snapshot.LastMessageId, Is.EqualTo(2));
            Assert.That(snapshot.Messages[0].ToolCalls, Has.Count.EqualTo(1));
            Assert.That(snapshot.Messages[0].ToolCalls[0].Completed, Is.True);
            Assert.That(snapshot.Messages[0].ToolCalls[0].ResultText, Is.EqualTo("ok"));
        }

        [Test]
        public void AgentInfo_PreservesRuntimeLabelsAndCapabilities()
        {
            var agent = CoderAgentInfo.FromJson(new JObject
            {
                ["id"] = "custom-reviewer",
                ["label"] = "Revisor do Projeto",
                ["description"] = "Agente dinâmico",
                ["profile"] = "review",
                ["planMode"] = true,
                ["specMode"] = false,
                ["maxTurns"] = 12,
                ["confirmBeforeIrreversible"] = true,
            });

            Assert.That(agent.Id, Is.EqualTo("custom-reviewer"));
            Assert.That(agent.Label, Is.EqualTo("Revisor do Projeto"));
            Assert.That(agent.PlanMode, Is.True);
            Assert.That(agent.MaxTurns, Is.EqualTo(12));
            Assert.That(agent.ConfirmBeforeIrreversible, Is.True);
        }
    }
}
