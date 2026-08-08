using System;
using System.Collections.Generic;
using System.Linq;
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
        private const string SessionA = "11111111-1111-4111-8111-111111111111";
        private const string SessionB = "22222222-2222-4222-8222-222222222222";
        private const string SessionC = "33333333-3333-4333-8333-333333333333";
        private const string MessageA = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        private const string MessageB = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

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
            Assert.That(CoderStreamEvent.Create("message_complete", "{}", null).IsTerminal, Is.False);
            Assert.That(CoderStreamEvent.Create("error", "{}", null).IsTerminal, Is.True);
            Assert.That(CoderStreamEvent.Create("text", "{}", null).IsTerminal, Is.False);
        }

        [Test]
        public void MessageComplete_IsProviderBoundaryAndDoneTerminatesRun()
        {
            var session = new ChatSession();
            var client = new CoderApiClient(session);
            var dispatch = typeof(CoderApiClient).GetMethod(
                "DispatchEventInner", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(dispatch, Is.Not.Null);

            session.StartAssistant();
            dispatch.Invoke(client, new object[]
            {
                "message_complete",
                "",
                new JObject
                {
                    ["message"] = new JObject
                    {
                        ["role"] = "assistant",
                        ["content"] = new JArray
                        {
                            new JObject { ["type"] = "text", ["text"] = "Vou usar uma tool." },
                            new JObject
                            {
                                ["type"] = "tool_use",
                                ["id"] = "tool-1",
                                ["name"] = "run_command",
                                ["input"] = new JObject { ["command"] = "dir" },
                            },
                        },
                    },
                },
            });

            Assert.That(session.IsRunning, Is.True);
            Assert.That(session.StreamingAssistant, Is.Not.Null);
            Assert.That(session.StreamingAssistant.Text, Is.EqualTo("Vou usar uma tool."));
            Assert.That(session.StreamingAssistant.ToolCalls, Has.Count.EqualTo(1));

            dispatch.Invoke(client, new object[] { "done", "", new JObject() });
            Assert.That(session.IsRunning, Is.False);
            Assert.That(session.StreamingAssistant, Is.Null);
        }

        [Test]
        public void FinishAssistant_DiscardsEmptyStreamingCard()
        {
            var session = new ChatSession();
            session.StartAssistant();
            Assert.That(session.Messages, Has.Count.EqualTo(1));

            session.FinishAssistant();

            Assert.That(session.Messages, Is.Empty);
            Assert.That(session.StreamingAssistant, Is.Null);
        }

        [Test]
        public void AbortAssistant_ClearsApprovalAndMarksRunningToolsInterrupted()
        {
            var session = new ChatSession();
            session.StartAssistant();
            session.AddToolCall("tool-1", "run_command", "{}");
            session.SetPendingToolApproval(new PendingToolApproval
            {
                ToolUseId = "tool-1",
                ToolName = "run_command",
            });

            session.AbortAssistant();

            Assert.That(session.IsRunning, Is.False);
            Assert.That(session.StreamingAssistant, Is.Null);
            Assert.That(session.Interactions, Is.Empty);
            Assert.That(session.ConnectionState, Is.EqualTo(ChatConnectionState.Idle));
            Assert.That(session.ConnectionDetail, Is.EqualTo("Execução interrompida"));
            Assert.That(session.Messages.Single().ToolCalls.Single().Completed, Is.True);
            Assert.That(session.Messages.Single().ToolCalls.Single().Failed, Is.True);
        }

        [Test]
        public void DoneAfterAbortRequest_UsesInterruptedTerminalState()
        {
            var session = new ChatSession();
            var client = new CoderApiClient(session);
            var abortRequested = typeof(CoderApiClient).GetField(
                "_abortRequested", BindingFlags.Instance | BindingFlags.NonPublic);
            var dispatch = typeof(CoderApiClient).GetMethod(
                "DispatchEventInner", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(abortRequested, Is.Not.Null);
            Assert.That(dispatch, Is.Not.Null);

            session.StartAssistant();
            session.AddToolCall("tool-1", "run_command", "{}");
            session.SetPendingToolApproval(new PendingToolApproval
            {
                ToolUseId = "tool-1",
                ToolName = "run_command",
            });
            abortRequested.SetValue(client, 1);

            dispatch.Invoke(client, new object[] { "done", "", new JObject() });

            Assert.That(client.IsStopping, Is.False);
            Assert.That(session.IsRunning, Is.False);
            Assert.That(session.Interactions, Is.Empty);
            Assert.That(session.ConnectionDetail, Is.EqualTo("Execução interrompida"));
            Assert.That(session.Messages.Single().ToolCalls.Single().Failed, Is.True);
        }

        [Test]
        public void ToolApprovalLayout_StacksLabelAndInputForNarrowDocks()
        {
            var element = new CoderChatInteractionElement(new PendingToolApproval
            {
                ToolUseId = "tool-1",
                ToolName = "run_command",
                InputJson = "{\"command\":\"dir\"}",
                RiskClass = "execute",
            });

            var field = element.Q<VisualElement>(className: "interaction-field");
            Assert.That(field, Is.Not.Null);
            Assert.That(field.Q<Label>(className: "interaction-field-label")?.text,
                Is.EqualTo("Motivo opcional (enviado ao agente)"));
            Assert.That(field.Q<TextField>(className: "interaction-input"), Is.Not.Null);
            Assert.That(element.Query<Button>().ToList().Select(button => button.text),
                Is.EquivalentTo(new[] { "Aprovar", "Negar" }));
        }

        [Test]
        public void PendingInteractiveTool_UsesDedicatedActionCardInsteadOfRawJson()
        {
            var session = new ChatSession();
            session.StartAssistant();
            session.AddToolCall("tool-plan-1", "submit_plan", "{\"plan\":\"1. Corrigir scroll\"}");

            Assert.That(session.CurrentPlan, Is.Not.Null);
            Assert.That(session.CurrentPlan.Plan, Is.EqualTo("1. Corrigir scroll"));
            Assert.That(session.CurrentPlan.RecoveredFromToolCall, Is.True);

            var message = session.StreamingAssistant;
            var elementType = typeof(CoderApiClient).Assembly.GetType(
                "Maicon.SceneAnalyzer.Editor.Window.CoderChatMessageElement", true);
            var element = (VisualElement)Activator.CreateInstance(elementType);
            elementType.GetMethod("Bind", BindingFlags.Instance | BindingFlags.Public)
                ?.Invoke(element, new object[] { message });

            Assert.That(element.Q<VisualElement>("tool-card-tool-plan-1"), Is.Null,
                "o JSON bruto da tool interativa não deve competir com o bloco acionável");

            session.SetPendingPlan(new PendingPlan { Id = "plan-real", Plan = "Plano oficial" });
            Assert.That(session.Interactions.OfType<PendingPlan>().Count(), Is.EqualTo(1));
            Assert.That(session.CurrentPlan.Id, Is.EqualTo("plan-real"));
            Assert.That(session.CurrentPlan.RecoveredFromToolCall, Is.False);

            session.CompleteToolCall("tool-plan-1", "approved", 12);
            Assert.That(session.CurrentPlan, Is.Null);
            elementType.GetMethod("Bind", BindingFlags.Instance | BindingFlags.Public)
                ?.Invoke(element, new object[] { message });
            Assert.That(element.Q<VisualElement>("tool-card-tool-plan-1"), Is.Not.Null,
                "a tool resolvida volta ao histórico como auditoria");
        }

        [Test]
        public void DecisionAndSpecEvents_CreateDedicatedActionBlocks()
        {
            var session = new ChatSession();
            var client = new CoderApiClient(session);
            var dispatch = typeof(CoderApiClient).GetMethod(
                "DispatchEventInner", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(dispatch, Is.Not.Null);

            dispatch.Invoke(client, new object[]
            {
                "decision",
                "",
                new JObject
                {
                    ["id"] = "decision-1",
                    ["header"] = "Estratégia",
                    ["questions"] = new JArray
                    {
                        new JObject
                        {
                            ["id"] = "storage",
                            ["question"] = "Onde persistir?",
                            ["options"] = new JArray
                            {
                                new JObject { ["label"] = "Arquivo", ["value"] = "file" },
                                new JObject { ["label"] = "Banco", ["value"] = "db" },
                            },
                        },
                    },
                },
            });
            dispatch.Invoke(client, new object[]
            {
                "spec",
                "",
                new JObject
                {
                    ["id"] = "spec-1",
                    ["name"] = "Chat UX",
                    ["body"] = "## Context",
                    ["spec_path"] = ".mcp/specs/chat-ux.md",
                },
            });

            Assert.That(session.CurrentDecision, Is.Not.Null);
            Assert.That(session.CurrentDecision.Questions.Single().Choices, Has.Count.EqualTo(2));
            Assert.That(session.CurrentSpec, Is.Not.Null);
            Assert.That(new CoderChatInteractionElement(session.CurrentDecision)
                .Query<Button>().ToList().Select(button => button.text),
                Does.Contain("Confirmar decisões"));
            Assert.That(new CoderChatInteractionElement(session.CurrentSpec)
                .Query<Button>().ToList().Select(button => button.text),
                Is.EquivalentTo(new[] { "Aprovar", "Solicitar alterações" }));
        }

        [Test]
        public void PlanResponse_UsesBackendFeedbackAndAbortContract()
        {
            var body = CoderApiClient.BuildPlanResponseBody("plan-1", false, "mude o escopo", true);

            Assert.That((bool)body["approved"], Is.False);
            Assert.That((bool)body["aborted"], Is.True);
            Assert.That((string)body["feedback"], Is.EqualTo("mude o escopo"));
            Assert.That(body["note"], Is.Null);
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
        public void SessionInvalidation_SeparatesStreamingFromChromeAndInteractions()
        {
            var session = new ChatSession();
            var invalidations = new List<ChatSessionInvalidation>();
            session.OnInvalidated += invalidations.Add;
            session.StartAssistant();
            invalidations.Clear();

            session.AppendAssistantText("delta");

            Assert.That(invalidations, Is.EqualTo(new[] { ChatSessionInvalidation.Transcript }));

            invalidations.Clear();
            session.ApplyBatch(() =>
            {
                session.AddSystemNote("nota");
                session.SetPendingPlan(new PendingPlan { Id = "plan-2", Plan = "Plano" });
            });
            Assert.That(invalidations, Has.Count.EqualTo(1));
            Assert.That(invalidations[0], Is.EqualTo(
                ChatSessionInvalidation.Transcript
                | ChatSessionInvalidation.Header
                | ChatSessionInvalidation.Interactions));
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
        public void ToolApprovalEvents_AreVisibleAndResolvableByToolUseId()
        {
            var session = new ChatSession();
            var client = new CoderApiClient(session);
            var dispatch = typeof(CoderApiClient).GetMethod(
                "DispatchEventInner", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(dispatch, Is.Not.Null);

            dispatch.Invoke(client, new object[]
            {
                "tool_approval_request",
                "",
                new JObject
                {
                    ["toolUseId"] = "tool-command-1",
                    ["toolName"] = "run_command",
                    ["argsSummary"] = "{\"command\":\"dir Assets\"}",
                    ["riskClass"] = "execute",
                    ["approvalKind"] = "standard",
                },
            });

            var approval = session.PendingToolApprovals.Single();
            Assert.That(approval.ToolUseId, Is.EqualTo("tool-command-1"));
            Assert.That(approval.ToolName, Is.EqualTo("run_command"));
            Assert.That(approval.ArgsSummary, Does.Contain("dir Assets"));
            Assert.That(session.ConnectionState, Is.EqualTo(ChatConnectionState.WaitingForApproval));

            dispatch.Invoke(client, new object[]
            {
                "tool_approval_resolved",
                "",
                new JObject { ["toolUseId"] = "tool-command-1" },
            });
            Assert.That(session.PendingToolApprovals, Is.Empty);
        }

        [Test]
        public void LiveReplay_PreservesPersistedTailAfterLatestUserMessage()
        {
            var session = new ChatSession();
            var user = new ChatMessage { Role = ChatRole.User, Text = "Faça", IsRemote = true };
            var staleAssistant = new ChatMessage { Role = ChatRole.Assistant, Text = "parcial", IsRemote = true };
            session.Messages.Add(user);
            session.Messages.Add(staleAssistant);
            session.AddSystemNote("nota local");
            session.SetPendingToolApproval(new PendingToolApproval
            {
                ToolUseId = "tool-1",
                ToolName = "run_command",
            });

            session.PrepareForLiveReplay();

            Assert.That(session.Messages, Has.Count.EqualTo(3));
            Assert.That(session.Messages[0], Is.SameAs(user));
            Assert.That(session.Messages[1], Is.SameAs(staleAssistant));
            Assert.That(session.Messages[2].Role, Is.EqualTo(ChatRole.System));
            Assert.That(session.Interactions, Is.Empty);
            Assert.That(CoderApiClient.IsActiveSessionStatus("in_progress"), Is.True);
            Assert.That(CoderApiClient.IsActiveSessionStatus("idle"), Is.False);
        }

        [Test]
        public void CursorlessReplay_PreservesSnapshotAndConsumesPersistedAssistantEvents()
        {
            var session = new ChatSession();
            var user = new ChatMessage
            {
                Role = ChatRole.User,
                Text = "Analise",
                IsRemote = true,
                RemoteId = MessageA,
                RemoteSessionId = SessionA,
            };
            var persistedAssistant = new ChatMessage
            {
                Role = ChatRole.Assistant,
                Text = "Trecho já persistido",
                IsRemote = true,
                RemoteId = MessageB,
                RemoteSessionId = SessionA,
            };
            persistedAssistant.ToolCalls.Add(new ChatToolCall
            {
                Id = "tool-old",
                Name = "read_file",
                InputJson = "{\"path\":\"Assets/Test.cs\"}",
            });
            session.Messages.Add(user);
            session.Messages.Add(persistedAssistant);

            var client = new CoderApiClient(session);
            var prepareReplay = typeof(CoderApiClient).GetMethod(
                "PrepareCursorlessReplay", BindingFlags.Instance | BindingFlags.NonPublic);
            var dispatch = typeof(CoderApiClient).GetMethod(
                "DispatchEventInner", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(prepareReplay, Is.Not.Null);
            Assert.That(dispatch, Is.Not.Null);

            prepareReplay.Invoke(client, null);
            session.StartAssistant();
            dispatch.Invoke(client, new object[]
            {
                "tool_call_start",
                "",
                new JObject
                {
                    ["id"] = "tool-old",
                    ["name"] = "read_file",
                    ["input"] = new JObject { ["path"] = "Assets/Test.cs" },
                },
            });
            dispatch.Invoke(client, new object[]
            {
                "tool_call_result",
                "",
                new JObject
                {
                    ["id"] = "tool-old",
                    ["result"] = "ok",
                    ["duration_ms"] = 12,
                },
            });
            dispatch.Invoke(client, new object[]
            {
                "message_complete",
                "",
                new JObject
                {
                    ["message"] = new JObject
                    {
                        ["content"] = new JArray
                        {
                            new JObject { ["type"] = "text", ["text"] = "Trecho já persistido" },
                            new JObject
                            {
                                ["type"] = "tool_use",
                                ["id"] = "tool-old",
                                ["name"] = "read_file",
                                ["input"] = new JObject { ["path"] = "Assets/Test.cs" },
                            },
                        },
                    },
                },
            });
            dispatch.Invoke(client, new object[]
            {
                "thinking",
                "",
                new JObject { ["text"] = "Trecho ainda vivo" },
            });

            Assert.That(session.Messages, Has.Count.EqualTo(3));
            Assert.That(session.Messages[0], Is.SameAs(user));
            Assert.That(session.Messages[1], Is.SameAs(persistedAssistant));
            Assert.That(session.Messages[1].ToolCalls.Single().Completed, Is.True);
            Assert.That(session.Messages[1].ToolCalls.Single().ResultText, Does.Contain("ok"));
            Assert.That(session.StreamingAssistant, Is.SameAs(session.Messages[2]));
            Assert.That(session.StreamingAssistant.ThinkingText, Is.EqualTo("Trecho ainda vivo"));
            Assert.That(session.StreamingAssistant.ToolCalls, Is.Empty);
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
            var content = "[unity-context — auto-injected by the Unity MCP plugin test]\n\n---\n\n"
                + "Mova o objeto\n\n\u0060\u0060\u0060unity-context\n"
                + context.ToString(Formatting.None)
                + "\n\u0060\u0060\u0060";
            var snapshot = CoderSessionSnapshot.FromJson(new JObject
            {
                ["session"] = new JObject { ["id"] = SessionA, ["status"] = "idle" },
                ["message_rows"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = MessageA,
                        ["seq"] = 7,
                        ["session_id"] = SessionA,
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
            Assert.That(session.Messages[0].RemoteId, Is.EqualTo(MessageA));
            Assert.That(session.Messages[0].RemoteSessionId, Is.EqualTo(SessionA));
            Assert.That(session.Messages[0].References, Has.Count.EqualTo(1));
        }

        [Test]
        public void SessionSnapshot_CorrelatesToolResultsWithAssistantCalls()
        {
            var snapshot = CoderSessionSnapshot.FromJson(new JObject
            {
                ["session"] = new JObject { ["id"] = SessionB },
                ["message_rows"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = MessageA,
                        ["seq"] = 1,
                        ["session_id"] = SessionB,
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
                        ["id"] = MessageB,
                        ["seq"] = 2,
                        ["session_id"] = SessionB,
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
        public void SessionHistory_UsesTailMetadataAndPrependsPagesWithoutDuplicates()
        {
            var snapshot = CoderSessionSnapshot.FromJson(new JObject
            {
                ["session"] = new JObject { ["id"] = SessionA },
                ["total_messages"] = 3,
                ["archived_count"] = 0,
                ["preceding_user_seq"] = 2,
                ["message_rows"] = new JArray
                {
                    MessageRow(MessageB, 3, SessionA, "assistant", "recente"),
                },
            });
            Assert.That(snapshot.HasEarlierMessages, Is.True);
            Assert.That(snapshot.EarliestMessageId, Is.EqualTo(3));

            var page = CoderMessagePage.FromJson(new JObject
            {
                ["next_before_seq"] = null,
                ["has_more"] = false,
                ["message_rows"] = new JArray
                {
                    MessageRow(MessageA, 1, SessionA, "user", "antiga"),
                    MessageRow(MessageB, 3, SessionA, "assistant", "recente"),
                },
            }, SessionA);
            var session = new ChatSession();
            session.MergeRemoteSnapshot(snapshot, true);
            session.MergeEarlierMessages(page);

            Assert.That(session.Messages.Select(message => message.Text),
                Is.EqualTo(new[] { "antiga", "recente" }));
            Assert.That(session.HasEarlierMessages, Is.False);
            Assert.That(session.NextHistoryBeforeSequence, Is.Null);
        }

        [Test]
        public void SessionSnapshot_RepeatedEquivalentMergeDoesNotNotifyOrRevise()
        {
            var root = new JObject
            {
                ["session"] = new JObject
                {
                    ["id"] = SessionC,
                    ["project_id"] = 3,
                    ["status"] = "idle",
                    ["agent"] = "plan",
                    ["updated_at"] = "2026-07-23T12:00:00Z",
                },
                ["message_rows"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = MessageA,
                        ["seq"] = 1,
                        ["session_id"] = SessionC,
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
                        ["id"] = MessageB,
                        ["seq"] = 2,
                        ["session_id"] = SessionC,
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
            AssertExpandedToolActivity(element);
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
            AssertExpandedToolActivity(element);
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
            Assert.That(state.CompleteCorrection(1000f, 1000f), Is.True);
            Assert.That(state.IsFollowingEnd, Is.True);
            Assert.That(state.CorrectionPending, Is.False);

            Assert.That(state.TryBeginCorrection(), Is.True);
            Assert.That(state.CompleteCorrection(700f, 1000f), Is.False,
                "o topo de uma mensagem maior que a viewport não é o fim do transcript");
            Assert.That(state.CorrectionPending, Is.True);
            Assert.That(state.CompleteCorrection(995f, 1000f), Is.True);

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
        public void ScrollState_ShortTranscriptUsesTopAsItsStableEnd()
        {
            Assert.That(CoderChatScrollState.NormalizeEnd(-540f), Is.Zero,
                "um transcript menor que a viewport não pode produzir offset negativo");
            Assert.That(CoderChatScrollState.NormalizeEnd(float.NaN), Is.Zero);
            Assert.That(CoderChatScrollState.NormalizeEnd(float.PositiveInfinity), Is.Zero);
            Assert.That(CoderChatScrollState.DistanceFromEnd(-540f, -540f), Is.Zero);
            Assert.That(CoderChatScrollState.DistanceFromEnd(0f, -540f), Is.Zero);
        }

        [Test]
        public void ComposerHeight_GrowsForExplicitAndWrappedLinesWithinBounds()
        {
            Assert.That(CoderChatWindow.EstimateComposerHeight("", 500f), Is.EqualTo(42f));
            Assert.That(CoderChatWindow.EstimateComposerHeight("linha 1\nlinha 2", 500f), Is.EqualTo(59f));
            Assert.That(CoderChatWindow.EstimateComposerHeight(new string('x', 2000), 260f), Is.EqualTo(160f));
        }

        [Test]
        public void UnityPresentation_CleansMarkdownAndDefersCollapsedToolPayloads()
        {
            Assert.That(ChatMessagePresentation.CleanMarkdown("## Validação\n- **Cena:** `Arena`"),
                Is.EqualTo("Validação\n• Cena: Arena"));
            Assert.That(ChatMessagePresentation.ToolName("ext_unity__query_scene"),
                Is.EqualTo("Unity · query scene"));

            var message = new ChatMessage { Role = ChatRole.Assistant };
            message.ToolCalls.Add(new ChatToolCall
            {
                Id = "tool-lazy",
                Name = "ext_unity__query_scene",
                InputJson = "PAYLOAD_NAO_RENDERIZAR_FECHADO",
            });
            var element = new CoderChatMessageElement();
            element.Bind(message);

            Assert.That(element.Query<Label>().ToList().Select(label => label.text),
                Does.Not.Contain("PAYLOAD_NAO_RENDERIZAR_FECHADO"));
            message.ToolCalls[0].Expanded = true;
            message.Touch();
            element.Bind(message);
            Assert.That(element.Query<Label>(className: "tool-code").ToList()
                    .Select(label => label.text),
                Does.Contain("PAYLOAD_NAO_RENDERIZAR_FECHADO"));
            Assert.That(element.ClassListContains("compact-activity"), Is.True);
        }

        [Test]
        public void UnityPresentation_RendersToolsAsSubtleActivityRows()
        {
            var message = new ChatMessage { Role = ChatRole.Assistant };
            message.ToolCalls.Add(new ChatToolCall
            {
                Id = "tool-subtle",
                Name = "ext_unity__query_scene",
                Completed = true,
                DurationMs = 19,
                InputJson = "{}",
            });

            var element = new CoderChatMessageElement();
            element.Bind(message);

            var activity = element.Q<VisualElement>("tool-card-tool-subtle");
            Assert.That(activity, Is.Not.Null);
            Assert.That(activity.ClassListContains("tool-activity"), Is.True);
            Assert.That(activity.ClassListContains("tool-card"), Is.False);
            Assert.That(activity.Q<Label>(className: "tool-activity-title").text,
                Is.EqualTo("Unity · query scene"));
            Assert.That(activity.Q<Label>(className: "tool-activity-status").text, Is.EqualTo("✓"));
            Assert.That(activity.Q<Label>(className: "tool-activity-duration").text, Is.EqualTo("19ms"));
            Assert.That(activity.Q<VisualElement>(className: "tool-activity-details").style.display.value,
                Is.EqualTo(DisplayStyle.None));
        }

        private static JObject MessageRow(
            string id, long sequence, string sessionId, string role, string text)
        {
            return new JObject
            {
                ["id"] = id,
                ["seq"] = sequence,
                ["session_id"] = sessionId,
                ["role"] = role,
                ["content_json"] = new JArray
                {
                    new JObject { ["type"] = "text", ["text"] = text },
                }.ToString(Formatting.None),
            };
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

        [Test]
        public void CreateSessionBody_CarriesComposerSettingsOnFirstTurn()
        {
            var body = CoderApiClient.BuildCreateSessionBody(
                114,
                "Unity",
                "kimi-code",
                "k3",
                "plan",
                new CoderSessionOptions
                {
                    ReasoningEffort = "high",
                    ExecutionMode = "ultra",
                    ThinkingEnabled = true,
                    MaxTurns = 40,
                    MaxAutoContinues = 9,
                });

            Assert.That((string)body["provider"], Is.EqualTo("kimi-code"));
            Assert.That((string)body["model"], Is.EqualTo("k3"));
            Assert.That((string)body["reasoning_effort"], Is.EqualTo("high"));
            Assert.That((string)body["execution_mode"], Is.EqualTo("ultra"));
            Assert.That((bool)body["thinking_enabled"], Is.True);
            Assert.That((int)body["max_turns"], Is.EqualTo(40));
            Assert.That((int)body["max_auto_continues"], Is.EqualTo(9));
        }

        [Test]
        public void UnityBuildAgentManifest_IsPluginManagedAndKeepsNativeBuildTools()
        {
            var manifest = UnityBuildAgentManifest.Build();

            Assert.That(UnityBuildAgentManifest.AgentId, Is.EqualTo("unity-build"));
            Assert.That(UnityBuildAgentManifest.SelectionId, Is.EqualTo("custom:unity-build"));
            Assert.That((string)manifest["managedBy"], Is.EqualTo("com.maicon.scene-analyzer"));
            Assert.That((string)manifest["managedVersion"], Is.EqualTo("0.18.1"));
            Assert.That((string)manifest["baseAgentId"], Is.EqualTo("build"));
            Assert.That(manifest["toolNames"].Type, Is.EqualTo(JTokenType.Null),
                "null inherits the native build tool registry instead of replacing it");
            CollectionAssert.Contains(manifest["requiredToolTiers"].Values<string>().ToArray(), "unity");
            Assert.That((int)manifest["visibleToolCap"], Is.EqualTo(64));
            Assert.That(manifest["skillIds"], Is.Empty,
                "routine Unity runs should not inject overlapping skill bodies before acting");
            var pins = manifest["pinnedToolNames"].Values<string>().ToArray();
            CollectionAssert.Contains(pins, "ext_unity__get_server_health");
            CollectionAssert.Contains(pins, "ext_unity__create_scene");
            CollectionAssert.Contains(pins, "ext_unity__save_scene");
            CollectionAssert.Contains(pins, "ext_unity__screenshot_object");
            StringAssert.Contains("real `ext_unity__*` round-trip", (string)manifest["prompt"]);
            StringAssert.Contains("do not create helper menus", (string)manifest["prompt"]);
        }

        [Test]
        public void CreateSessionBody_PreservesPluginManagedUnityAgentSelection()
        {
            var body = CoderApiClient.BuildCreateSessionBody(
                114,
                "Unity Build benchmark",
                "openai-chatgpt",
                "gpt-5.6-sol",
                UnityBuildAgentManifest.SelectionId,
                new CoderSessionOptions());

            Assert.That((string)body["agent"], Is.EqualTo(UnityBuildAgentManifest.SelectionId));
        }

        [Test]
        public void SessionInfo_UsesCanonicalUuidAndRejectsLegacyNumericIdentity()
        {
            var current = CoderSessionInfo.FromJson(new JObject
            {
                ["id"] = SessionA.ToUpperInvariant(),
                ["project_id"] = 112,
                ["title"] = "Unity",
            });
            var legacy = CoderSessionInfo.FromJson(new JObject
            {
                ["id"] = 1326,
                ["project_id"] = 112,
            });

            Assert.That(current.Id, Is.EqualTo(SessionA));
            Assert.That(current.ProjectId, Is.EqualTo(112));
            Assert.That(legacy.Id, Is.Empty);
        }

        [Test]
        public void SessionInfo_ProjectsAdvancedComposerSettings()
        {
            var session = CoderSessionInfo.FromJson(new JObject
            {
                ["id"] = SessionA,
                ["provider"] = "kimi-code",
                ["model"] = "k3",
                ["reasoning_effort_override"] = "max",
                ["execution_mode_override"] = "ultra",
                ["thinking_enabled"] = true,
                ["plan_depth_override"] = "complex",
                ["tool_output_cap_level"] = "high",
                ["tool_permission_mode"] = "unrestricted",
                ["response_language_override"] = "Português (Brasil)",
                ["output_style_override"] = "explanatory",
                ["max_turns"] = 80,
                ["max_auto_continues"] = 12,
            });

            Assert.That(session.ReasoningEffortOverride, Is.EqualTo("max"));
            Assert.That(session.ExecutionModeOverride, Is.EqualTo("ultra"));
            Assert.That(session.ThinkingEnabled, Is.True);
            Assert.That(session.PlanDepthOverride, Is.EqualTo("complex"));
            Assert.That(session.ToolOutputCapLevel, Is.EqualTo("high"));
            Assert.That(session.ToolPermissionMode, Is.EqualTo("unrestricted"));
            Assert.That(session.MaxTurns, Is.EqualTo(80));
            Assert.That(session.MaxAutoContinues, Is.EqualTo(12));
        }

        private static void AssertExpandedToolActivity(VisualElement element)
        {
            var foldouts = new List<Foldout>();
            element.Query<Foldout>().ForEach(foldouts.Add);
            Assert.That(foldouts, Has.Count.EqualTo(1));
            Assert.That(foldouts[0].value, Is.True);
            Assert.That(element.Q<VisualElement>(className: "tool-activity-details").style.display.value,
                Is.EqualTo(DisplayStyle.Flex));
        }

        [Test]
        public void DefaultPreamble_UsesRegisteredUnityMcpPrefix()
        {
            var preamble = CoderApiClient.BuildDefaultPreamble("C:/UnityProject");

            StringAssert.Contains("ext_unity__", preamble);
            StringAssert.Contains("ext_unity__get_server_health", preamble);
            StringAssert.Contains(UnityBuildAgentManifest.SelectionId, preamble);
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
