using System;
using System.Collections.Generic;
using System.Reflection;
using Maicon.SceneAnalyzer.Editor.Chat;
using Maicon.SceneAnalyzer.Editor.Core;
using Maicon.SceneAnalyzer.Editor.Window;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.UIElements;

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
        public void SessionSnapshot_RepeatedEquivalentMergeDoesNotNotifyOrRevise()
        {
            var root = new JObject
            {
                ["session"] = new JObject
                {
                    ["id"] = 17,
                    ["project_id"] = 3,
                    ["status"] = "idle",
                    ["agent"] = "plan",
                    ["updated_at"] = "2026-07-23T12:00:00Z",
                },
                ["message_rows"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = 1,
                        ["session_id"] = 17,
                        ["role"] = "assistant",
                        ["created_at"] = "2026-07-23T12:00:00Z",
                        ["content_json"] = new JArray
                        {
                            new JObject { ["type"] = "thinking", ["text"] = "Analisando" },
                            new JObject { ["type"] = "text", ["text"] = "Pronto" },
                            new JObject
                            {
                                ["type"] = "tool_use",
                                ["id"] = "tool-1",
                                ["name"] = "read_file",
                                ["input"] = new JObject { ["path"] = "Assets/Test.cs" },
                            },
                        }.ToString(Formatting.None),
                    },
                    new JObject
                    {
                        ["id"] = 2,
                        ["session_id"] = 17,
                        ["role"] = "tool",
                        ["created_at"] = "2026-07-23T12:00:01Z",
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
            };
            var session = new ChatSession();
            var notifications = 0;
            session.OnChanged += () => notifications++;

            session.MergeRemoteSnapshot(
                CoderSessionSnapshot.FromJson((JObject)root.DeepClone()),
                replaceConversation: true);
            session.Messages[0].ThinkingExpanded = true;
            session.Messages[0].ToolCalls[0].Expanded = true;
            var sessionRevision = session.Revision;
            var messageRevision = session.Messages[0].Revision;
            notifications = 0;

            session.MergeRemoteSnapshot(
                CoderSessionSnapshot.FromJson((JObject)root.DeepClone()),
                replaceConversation: true);

            Assert.That(notifications, Is.Zero);
            Assert.That(session.Revision, Is.EqualTo(sessionRevision));
            Assert.That(session.Messages[0].Revision, Is.EqualTo(messageRevision));
            Assert.That(session.Messages[0].ThinkingExpanded, Is.True);
            Assert.That(session.Messages[0].ToolCalls[0].Expanded, Is.True);
        }

        [Test]
        public void MessageElement_RebindPreservesExpandedThinkingAndToolCards()
        {
            var message = new ChatMessage
            {
                Role = ChatRole.Assistant,
                ThinkingText = "Raciocínio detalhado",
                ThinkingExpanded = true,
            };
            message.ToolCalls.Add(new ChatToolCall
            {
                Id = "tool-1",
                Name = "read_file",
                InputJson = "{}",
                Expanded = true,
            });

            var elementType = typeof(CoderApiClient).Assembly.GetType(
                "Maicon.SceneAnalyzer.Editor.Window.CoderChatMessageElement", true);
            var element = (VisualElement)Activator.CreateInstance(elementType);
            var bind = elementType.GetMethod("Bind", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(bind, Is.Not.Null);

            message.References.Add(new ChatReference
            {
                Label = "Player",
                Target = new ToolTargetRef { Kind = "asset", AssetPath = "Assets/Player.prefab" },
            });

            bind.Invoke(element, new object[] { message });
            AssertExpandedFoldouts(element);
            var originalToolCard = element.Q<VisualElement>("tool-card-tool-1");
            var originalReferenceChip = element.Q<VisualElement>("reference-chip-path:Assets/Player.prefab");
            Assert.That(originalToolCard, Is.Not.Null);
            Assert.That(originalReferenceChip, Is.Not.Null);

            message.Text = "Resposta parcial";
            message.Touch();
            bind.Invoke(element, new object[] { message });

            Assert.That(element.Q<VisualElement>("tool-card-tool-1"), Is.SameAs(originalToolCard));
            Assert.That(element.Q<VisualElement>("reference-chip-path:Assets/Player.prefab"), Is.SameAs(originalReferenceChip));

            var remote = new ChatMessage
            {
                Role = ChatRole.Assistant,
                ThinkingText = "Raciocínio detalhado",
                IsRemote = true,
            };
            remote.ToolCalls.Add(new ChatToolCall
            {
                Id = "tool-1",
                Name = "read_file",
                InputJson = "{}",
                ResultText = "ok",
                Completed = true,
            });
            remote.References.Add(new ChatReference
            {
                Label = "Player",
                Target = new ToolTargetRef { Kind = "asset", AssetPath = "Assets/Player.prefab" },
            });

            Assert.That(message.CopyRemoteContentFrom(remote), Is.True);
            Assert.That(message.ThinkingExpanded, Is.True);
            Assert.That(message.ToolCalls[0].Expanded, Is.True);

            bind.Invoke(element, new object[] { message });
            AssertExpandedFoldouts(element);
            Assert.That(element.Q<VisualElement>("tool-card-tool-1"), Is.SameAs(originalToolCard));
            Assert.That(element.Q<VisualElement>("reference-chip-path:Assets/Player.prefab"), Is.SameAs(originalReferenceChip));
        }

        [Test]
        public void MessageElement_ReconcileKeepsDuplicateAndFallbackKeysDistinct()
        {
            var message = new ChatMessage { Role = ChatRole.Assistant };
            message.ToolCalls.Add(new ChatToolCall { Id = "index-1", Name = "explicit" });
            message.ToolCalls.Add(new ChatToolCall { Name = "fallback" });
            message.ToolCalls.Add(new ChatToolCall { Id = "duplicate", Name = "first duplicate" });
            message.ToolCalls.Add(new ChatToolCall { Id = "duplicate", Name = "second duplicate" });
            message.References.Add(new ChatReference
            {
                Label = "Player A",
                Target = new ToolTargetRef { Kind = "asset", AssetPath = "Assets/Player.prefab" },
            });
            message.References.Add(new ChatReference
            {
                Label = "Player B",
                Target = new ToolTargetRef { Kind = "asset", AssetPath = "Assets/Player.prefab" },
            });

            var elementType = typeof(CoderApiClient).Assembly.GetType(
                "Maicon.SceneAnalyzer.Editor.Window.CoderChatMessageElement", true);
            var element = (VisualElement)Activator.CreateInstance(elementType);
            var bind = elementType.GetMethod("Bind", BindingFlags.Instance | BindingFlags.Public);
            bind.Invoke(element, new object[] { message });

            var toolList = element.Q<VisualElement>(className: "tool-list");
            var referenceList = element.Q<VisualElement>(className: "message-references");
            Assert.That(toolList.childCount, Is.EqualTo(4));
            Assert.That(referenceList.childCount, Is.EqualTo(2));
            var explicitCard = element.Q<VisualElement>("tool-card-index-1");
            var fallbackCard = element.Q<VisualElement>("tool-card-fallback-index-1");
            var duplicateCard = element.Q<VisualElement>("tool-card-duplicate-duplicate-1");
            var duplicateReference = element.Q<VisualElement>("reference-chip-path:Assets/Player.prefab-duplicate-1");
            Assert.That(explicitCard, Is.Not.Null);
            Assert.That(fallbackCard, Is.Not.Null.And.Not.SameAs(explicitCard));
            Assert.That(duplicateCard, Is.Not.Null);
            Assert.That(duplicateReference, Is.Not.Null);

            message.Touch();
            bind.Invoke(element, new object[] { message });

            Assert.That(toolList.childCount, Is.EqualTo(4));
            Assert.That(referenceList.childCount, Is.EqualTo(2));
            Assert.That(element.Q<VisualElement>("tool-card-index-1"), Is.SameAs(explicitCard));
            Assert.That(element.Q<VisualElement>("tool-card-fallback-index-1"), Is.SameAs(fallbackCard));
            Assert.That(element.Q<VisualElement>("tool-card-duplicate-duplicate-1"), Is.SameAs(duplicateCard));
            Assert.That(element.Q<VisualElement>("reference-chip-path:Assets/Player.prefab-duplicate-1"), Is.SameAs(duplicateReference));
        }

        [Test]
        public void UiInvalidation_CoalescesUntilConsumedOrCancelled()
        {
            var state = new CoderChatInvalidationState();

            Assert.That(state.TrySchedule(), Is.True);
            Assert.That(state.TrySchedule(), Is.False);
            Assert.That(state.IsScheduled, Is.True);

            state.Consume();
            Assert.That(state.IsScheduled, Is.False);
            Assert.That(state.TrySchedule(), Is.True);

            state.Cancel();
            Assert.That(state.IsScheduled, Is.False);
        }

        [Test]
        public void TranscriptAppend_DetectsOnlyStrictReferencePrefixGrowth()
        {
            var first = new object();
            var second = new object();
            var replacement = new object();

            Assert.That(CoderChatWindow.IsStrictReferenceAppend(
                new[] { first }, new[] { first, second }), Is.True);
            Assert.That(CoderChatWindow.IsStrictReferenceAppend(
                new[] { first }, new[] { replacement, second }), Is.False);
            Assert.That(CoderChatWindow.IsStrictReferenceAppend(
                new[] { first }, new[] { first }), Is.False);
        }

        [Test]
        public void ScrollState_PreservesUserHistoryUntilFollowingIsResumed()
        {
            var state = new CoderChatScrollState();

            Assert.That(state.IsFollowingEnd, Is.True);
            Assert.That(state.TryBeginCorrection(), Is.True);
            Assert.That(state.TryBeginCorrection(), Is.False);
            state.CompleteCorrection();
            Assert.That(state.IsFollowingEnd, Is.True);
            Assert.That(state.CorrectionPending, Is.False);

            state.ObserveUserPosition(700f, 1000f);
            Assert.That(state.IsFollowingEnd, Is.False);
            Assert.That(state.TryBeginCorrection(), Is.False);

            state.ResumeFollowing();
            Assert.That(state.TryBeginCorrection(), Is.True);
            state.CancelCorrection();
            state.SuspendFollowing();
            Assert.That(state.IsFollowingEnd, Is.False);

            state.BeginUserDrag();
            Assert.That(state.IsUserDragging, Is.True);
            state.ObserveUserPosition(980f, 1000f);
            Assert.That(state.IsFollowingEnd, Is.False);

            state.EndUserDrag();
            Assert.That(state.IsUserDragging, Is.False);
            state.ObserveUserPosition(980f, 1000f);
            Assert.That(state.IsFollowingEnd, Is.True);
            Assert.That(CoderChatScrollState.DistanceFromEnd(980f, 1000f), Is.EqualTo(20f));
        }

        [Test]
        public void ComposerHeight_GrowsForExplicitAndWrappedLinesWithinBounds()
        {
            Assert.That(CoderChatWindow.EstimateComposerHeight("", 500f), Is.EqualTo(42f));
            Assert.That(CoderChatWindow.EstimateComposerHeight("linha 1\nlinha 2", 500f), Is.EqualTo(59f));
            Assert.That(CoderChatWindow.EstimateComposerHeight(new string('x', 2000), 260f), Is.EqualTo(160f));
        }

        [Test]
        public void MessageBody_AgentOwnsProfileAndLegacyFallbackIsNormalized()
        {
            var agentBody = CoderApiClient.BuildMessageBody("Olá", "bootstrap", "plan", true);

            Assert.That((string)agentBody["agent"], Is.EqualTo("plan"));
            Assert.That(agentBody["profile"], Is.Null);
            Assert.That((bool?)agentBody["thinking"], Is.True);

            var fallbackBody = CoderApiClient.BuildMessageBody("Olá", "review", null, false);
            Assert.That(fallbackBody["agent"], Is.Null);
            Assert.That((string)fallbackBody["profile"], Is.EqualTo("auto"));
            Assert.That(fallbackBody["thinking"], Is.Null);
        }

        private static void AssertExpandedFoldouts(VisualElement element)
        {
            var foldouts = new List<Foldout>();
            element.Query<Foldout>().ForEach(foldouts.Add);
            Assert.That(foldouts, Has.Count.EqualTo(2));
            Assert.That(foldouts[0].value, Is.True);
            Assert.That(foldouts[1].value, Is.True);
        }

        [Test]
        public void DefaultPreamble_UsesRegisteredUnityMcpPrefix()
        {
            var preamble = CoderApiClient.BuildDefaultPreamble("C:/UnityProject");

            StringAssert.Contains("ext_unity__", preamble);
            StringAssert.DoesNotContain("ext_unity_editor__", preamble);
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
