using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Maicon.SceneAnalyzer.Bridge
{
    /// In-scene MonoBehaviour that lets editor-side AI tools dispatch named
    /// commands into a running game. The editor enqueues a BridgeScript; this
    /// component drains the queue in Update, invokes registered handlers in
    /// order, captures screenshots / log anchors when requested, and marks
    /// each script complete so the editor channel can return the results to
    /// the calling MCP tool.
    ///
    /// Game-agnostic: this component does not know about your game. You wire
    /// it up by calling `McpCommandBridge.Register("jump", (json, complete) => { ... });`
    /// from your own MonoBehaviour's Start(). See Runtime/Bridge/README.md.
    ///
    /// Threading: Register/UnregisterHandler are thread-safe. Submit (called
    /// from the editor) and Update (Unity main thread) coordinate through a
    /// ConcurrentQueue. Handler invocation is always on the Unity main thread.
    public sealed class McpCommandBridge : MonoBehaviour
    {
        public delegate void CommandHandler(string payloadJson, Action<bool, string> complete);

        [Tooltip("When true, the bridge GameObject survives runtime scene loads " +
                 "(DontDestroyOnLoad). Set by bridge_install; you can flip this in " +
                 "the inspector if you'd rather the bridge be scene-scoped.")]
        public bool dontDestroyOnLoad = true;

        // Static accessors so the editor side can talk to "the" bridge without
        // a scene lookup. Awake binds; OnDestroy clears.
        public static McpCommandBridge Instance { get; private set; }
        public static bool Exists => Instance != null;

        private readonly ConcurrentQueue<BridgeScript> _inbox = new();
        private readonly Dictionary<string, CommandHandler> _handlers = new();
        private readonly object _handlersLock = new();

        // For introspection from the editor.
        public sealed class DispatchInfo
        {
            public string LastStepName;
            public int LastFrameCount;
            public bool LastOk;
            public string LastError;
        }
        public DispatchInfo Last { get; } = new DispatchInfo();

        public int QueuedScripts => _inbox.Count;
        public IReadOnlyCollection<string> RegisteredHandlerNames
        {
            get
            {
                lock (_handlersLock) return new List<string>(_handlers.Keys);
            }
        }

        // ---- Handler registration ---------------------------------------------------
        public static void Register(string name, CommandHandler handler)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required");
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (Instance == null)
            {
                Debug.LogWarning($"[scene-analyzer] McpCommandBridge.Register('{name}') called before the bridge exists in the scene. " +
                                 "Either register from a script attached to the same GameObject, or call bridge_install from the AI before registering.");
                return;
            }
            lock (Instance._handlersLock)
            {
                Instance._handlers[name] = handler;
            }
        }

        public static bool Unregister(string name)
        {
            if (Instance == null) return false;
            lock (Instance._handlersLock)
            {
                return Instance._handlers.Remove(name);
            }
        }

        // ---- Editor → runtime submission --------------------------------------------
        public void Submit(BridgeScript script)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));
            _inbox.Enqueue(script);
        }

        // ---- Lifecycle --------------------------------------------------------------
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[scene-analyzer] Duplicate McpCommandBridge detected — destroying the new one.");
                UnityEngine.Object.Destroy(gameObject);
                return;
            }
            Instance = this;
            if (dontDestroyOnLoad && Application.isPlaying)
            {
                DontDestroyOnLoad(gameObject);
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            // Pull one script at a time and run it as a coroutine. Scripts run
            // sequentially — submitting many in flight is allowed but they
            // serialize, which keeps the timeline interpretation predictable.
            if (_running != null) return;
            if (!_inbox.TryDequeue(out var script)) return;
            _running = StartCoroutine(RunScript(script));
        }

        private Coroutine _running;

        private IEnumerator RunScript(BridgeScript script)
        {
            script.StartedExecution = true;
            try
            {
                for (int i = 0; i < script.Steps.Count; i++)
                {
                    var step = script.Steps[i];
                    var result = new BridgeStepResult
                    {
                        Name = step.Name,
                        FrameAtStart = Time.frameCount,
                    };

                    // Inter-step delay (frame-based for determinism).
                    for (int f = 0; f < step.DelayFrames; f++) yield return null;

                    // Log anchor BEFORE invocation if requested.
                    if (step.MarkLogAnchorBefore)
                    {
                        result.LogAnchorSeq = TryGetEditorLogSeq();
                    }

                    // Resolve handler.
                    CommandHandler handler = null;
                    lock (_handlersLock) _handlers.TryGetValue(step.Name, out handler);

                    if (handler == null)
                    {
                        result.Ok = false;
                        result.Error = "HANDLER_NOT_REGISTERED";
                        result.FrameAtEnd = Time.frameCount;
                        script.Results.Add(result);
                        UpdateLast(result);
                        if (script.StopOnError) break;
                        else continue;
                    }

                    var sw = Stopwatch.StartNew();
                    bool completed = false;
                    bool ok = false;
                    string err = null;
                    try
                    {
                        handler(step.PayloadJson ?? "", (success, errorMsg) =>
                        {
                            completed = true;
                            ok = success;
                            err = errorMsg;
                        });
                    }
                    catch (Exception e)
                    {
                        completed = true;
                        ok = false;
                        err = $"handler threw: {e.GetType().Name}: {e.Message}";
                    }

                    // Cooperative wait for async handlers — they have until end
                    // of the current frame plus a safety cap. Tweakable later.
                    int safetyFrames = 600;
                    while (!completed && safetyFrames-- > 0) yield return null;

                    sw.Stop();
                    result.Ok = completed && ok;
                    result.Error = completed ? err : "HANDLER_TIMEOUT_NO_COMPLETE_CALLBACK";
                    result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
                    result.FrameAtEnd = Time.frameCount;

                    // Optional screenshot AFTER the step. Render path is left
                    // for the editor side to ask via screenshot_game_view if it
                    // wants the same camera-selection logic. To keep the bridge
                    // self-contained for "send_runtime_commands { captureScreenshotAfter:true }",
                    // we sample Camera.main here at end-of-frame.
                    if (step.CaptureScreenshotAfter)
                    {
                        yield return new WaitForEndOfFrame();
                        TryCapture(result);
                    }

                    script.Results.Add(result);
                    UpdateLast(result);

                    if (!result.Ok && script.StopOnError) break;
                }
            }
            finally
            {
                script.IsComplete = true;
                _running = null;
            }
        }

        private void TryCapture(BridgeStepResult result)
        {
            var cam = Camera.main;
            if (cam == null)
            {
                // Fall back to highest-depth enabled.
                float bestDepth = float.NegativeInfinity;
                foreach (var c in Camera.allCameras)
                {
                    if (c == null || !c.enabled) continue;
                    if (c.depth > bestDepth) { cam = c; bestDepth = c.depth; }
                }
            }
            if (cam == null) return;

            const int W = 1280, H = 720;
            RenderTexture rt = null;
            Texture2D tex = null;
            try
            {
                rt = RenderTexture.GetTemporary(W, H, 24, RenderTextureFormat.ARGB32);
                var prevTarget = cam.targetTexture;
                var prevActive = RenderTexture.active;
                try
                {
                    cam.targetTexture = rt;
                    cam.Render();
                }
                finally { cam.targetTexture = prevTarget; }
                RenderTexture.active = rt;
                tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                result.ScreenshotPng = tex.EncodeToPNG();
                result.ScreenshotWidth = W;
                result.ScreenshotHeight = H;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[scene-analyzer] bridge screenshot failed: {e.Message}");
            }
            finally
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
        }

        private int TryGetEditorLogSeq()
        {
            // ConsoleCapture is editor-only. Best-effort reflection so the
            // runtime asmdef stays editor-independent in builds. In play
            // mode inside the editor, the type is loaded.
            try
            {
                var t = Type.GetType("Maicon.SceneAnalyzer.Editor.Core.ConsoleCapture, com.maicon.scene-analyzer.Editor");
                if (t == null) return 0;
                var prop = t.GetProperty("CurrentSeq",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (prop == null) return 0;
                return (int)prop.GetValue(null);
            }
            catch { return 0; }
        }

        private void UpdateLast(BridgeStepResult r)
        {
            Last.LastStepName = r.Name;
            Last.LastFrameCount = r.FrameAtEnd;
            Last.LastOk = r.Ok;
            Last.LastError = r.Error;
        }
    }
}
