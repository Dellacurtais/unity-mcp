using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Maicon.SceneAnalyzer.Bridge
{
    public sealed class McpCommandBridge : MonoBehaviour
    {
        public delegate void CommandHandler(string payloadJson, Action<bool, string> complete);

        [Tooltip("Keep the bridge alive across runtime scene loads.")]
        public bool dontDestroyOnLoad = true;
        [Min(1)] public int maxQueuedScripts = 32;
        [Min(1)] public int defaultHandlerTimeoutFrames = 600;
        [Min(0.1f)] public float defaultHandlerTimeoutSeconds = 10f;

        public static McpCommandBridge Instance { get; private set; }
        public static bool Exists => Instance != null;

        private readonly ConcurrentQueue<BridgeScript> _inbox = new ConcurrentQueue<BridgeScript>();
        private readonly ConcurrentDictionary<int, BridgeScript> _scripts = new ConcurrentDictionary<int, BridgeScript>();
        private readonly Dictionary<string, CommandHandler> _handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal);
        private readonly object _handlersLock = new object();
        private Coroutine _running;
        private BridgeScript _runningScript;
        private bool _shuttingDown;

        public sealed class DispatchInfo
        {
            public string LastStepName;
            public int LastFrameCount;
            public bool LastOk;
            public string LastError;
            public int LastScriptId;
            public BridgeScriptStatus LastScriptStatus;
        }

        public DispatchInfo Last { get; } = new DispatchInfo();
        public int QueuedScripts => _inbox.Count;
        public int ActiveScripts => _scripts.Count;
        public int RunningScriptId => _runningScript != null ? _runningScript.Id : 0;
        public IReadOnlyCollection<string> RegisteredHandlerNames
        {
            get { lock (_handlersLock) return new List<string>(_handlers.Keys); }
        }

        public static void Register(string name, CommandHandler handler)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required", nameof(name));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (Instance == null)
            {
                Debug.LogWarning($"[scene-analyzer] McpCommandBridge.Register('{name}') called before the bridge exists.");
                return;
            }
            lock (Instance._handlersLock) Instance._handlers[name] = handler;
        }

        public static bool Unregister(string name)
        {
            if (Instance == null) return false;
            lock (Instance._handlersLock) return Instance._handlers.Remove(name);
        }

        public void Submit(BridgeScript script)
        {
            if (!TrySubmit(script, out var error)) throw new InvalidOperationException(error);
        }

        public bool TrySubmit(BridgeScript script, out string error)
        {
            error = null;
            if (script == null) { error = "SCRIPT_REQUIRED"; return false; }
            if (script.CancelRequested || script.IsComplete) { error = "SCRIPT_CANCELLED"; return false; }
            if (_shuttingDown || !isActiveAndEnabled) { error = "BRIDGE_UNAVAILABLE"; return false; }
            if (script.Steps == null || script.Steps.Count == 0) { error = "SCRIPT_EMPTY"; return false; }
            if (_scripts.Count >= Mathf.Max(1, maxQueuedScripts)) { error = "BRIDGE_QUEUE_FULL"; return false; }
            if (!_scripts.TryAdd(script.Id, script)) { error = "DUPLICATE_SCRIPT_ID"; return false; }
            script.SubmittedUtcTicks = DateTime.UtcNow.Ticks;
            _inbox.Enqueue(script);
            return true;
        }

        public bool Cancel(int scriptId, string reason = "CANCELLED_BY_CALLER")
        {
            if (!_scripts.TryGetValue(scriptId, out var script)) return false;
            script.RequestCancel(reason);
            if (script.Status == BridgeScriptStatus.Queued) Finish(script, BridgeScriptStatus.Cancelled, reason);
            return true;
        }

        public int CancelAll(string reason = "BRIDGE_STOPPED")
        {
            int count = 0;
            foreach (var pair in _scripts)
            {
                if (!pair.Value.IsComplete)
                {
                    count++;
                    pair.Value.RequestCancel(reason);
                    if (pair.Value.Status == BridgeScriptStatus.Queued)
                        Finish(pair.Value, BridgeScriptStatus.Cancelled, reason);
                }
            }
            return count;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[scene-analyzer] Duplicate McpCommandBridge detected; destroying the new one.");
                Destroy(gameObject);
                return;
            }
            Instance = this;
            _shuttingDown = false;
            if (dontDestroyOnLoad && Application.isPlaying) DontDestroyOnLoad(gameObject);
        }

        private void OnEnable() { _shuttingDown = false; }

        private void OnDisable()
        {
            _shuttingDown = true;
            CancelAll("BRIDGE_DISABLED");
            if (_running != null) StopCoroutine(_running);
            if (_runningScript != null && !_runningScript.IsComplete)
                Finish(_runningScript, BridgeScriptStatus.Cancelled, "BRIDGE_DISABLED");
            _running = null;
            _runningScript = null;
        }

        private void OnDestroy()
        {
            _shuttingDown = true;
            CancelAll("BRIDGE_DESTROYED");
            lock (_handlersLock) _handlers.Clear();
            if (Instance == this) Instance = null;
        }

        private void OnApplicationQuit()
        {
            _shuttingDown = true;
            CancelAll("APPLICATION_QUIT");
        }

        private void Update()
        {
            if (_running != null || _shuttingDown) return;
            while (_inbox.TryDequeue(out var script))
            {
                if (script.IsComplete) continue;
                if (script.CancelRequested)
                {
                    Finish(script, BridgeScriptStatus.Cancelled, script.TerminalError ?? "CANCELLED");
                    continue;
                }
                _runningScript = script;
                _running = StartCoroutine(RunScript(script));
                return;
            }
        }

        private IEnumerator RunScript(BridgeScript script)
        {
            if (!script.TryStart())
            {
                Finish(script, script.CancelRequested ? BridgeScriptStatus.Cancelled : BridgeScriptStatus.Failed,
                    script.TerminalError ?? "SCRIPT_COULD_NOT_START");
                yield break;
            }

            BridgeScriptStatus terminal = BridgeScriptStatus.Completed;
            string terminalError = null;
            try
            {
                for (int i = 0; i < script.Steps.Count; i++)
                {
                    if (script.CancelRequested) { terminal = BridgeScriptStatus.Cancelled; terminalError = script.TerminalError ?? "CANCELLED"; break; }
                    var step = script.Steps[i];
                    var result = new BridgeStepResult { Name = step.Name, FrameAtStart = Time.frameCount };

                    for (int frame = 0; frame < Mathf.Max(0, step.DelayFrames); frame++)
                    {
                        if (script.CancelRequested) break;
                        yield return null;
                    }
                    if (script.CancelRequested) { terminal = BridgeScriptStatus.Cancelled; terminalError = script.TerminalError ?? "CANCELLED"; break; }

                    if (step.MarkLogAnchorBefore) result.LogAnchorSeq = TryGetEditorLogSeq();
                    CommandHandler handler;
                    lock (_handlersLock) _handlers.TryGetValue(step.Name, out handler);
                    if (handler == null)
                    {
                        CompleteStep(script, result, false, "HANDLER_NOT_REGISTERED", 0f);
                        if (script.StopOnError) { terminal = BridgeScriptStatus.Failed; terminalError = result.Error; break; }
                        continue;
                    }

                    var stopwatch = Stopwatch.StartNew();
                    bool completed = false;
                    bool succeeded = false;
                    bool acceptCallback = true;
                    string error = null;
                    try
                    {
                        handler(step.PayloadJson ?? string.Empty, (ok, message) =>
                        {
                            if (!acceptCallback || completed) return;
                            completed = true;
                            succeeded = ok;
                            error = message;
                        });
                    }
                    catch (Exception exception)
                    {
                        completed = true;
                        error = $"handler threw: {exception.GetType().Name}: {exception.Message}";
                    }

                    int timeoutFrames = step.TimeoutFrames > 0 ? step.TimeoutFrames : Mathf.Max(1, defaultHandlerTimeoutFrames);
                    float timeoutSeconds = step.TimeoutSeconds > 0f ? step.TimeoutSeconds : Mathf.Max(0.1f, defaultHandlerTimeoutSeconds);
                    int startFrame = Time.frameCount;
                    float startRealtime = Time.realtimeSinceStartup;
                    while (!completed && !script.CancelRequested
                        && Time.frameCount - startFrame < timeoutFrames
                        && Time.realtimeSinceStartup - startRealtime < timeoutSeconds)
                        yield return null;

                    acceptCallback = false;
                    stopwatch.Stop();
                    if (script.CancelRequested)
                    {
                        terminal = BridgeScriptStatus.Cancelled;
                        terminalError = script.TerminalError ?? "CANCELLED";
                        break;
                    }
                    if (!completed)
                    {
                        error = $"HANDLER_TIMEOUT: no completion callback within {timeoutFrames} frames/{timeoutSeconds:F1}s";
                        succeeded = false;
                    }

                    result.Ok = completed && succeeded;
                    result.Error = result.Ok ? null : error ?? "HANDLER_FAILED";
                    result.DurationMs = (float)stopwatch.Elapsed.TotalMilliseconds;
                    result.FrameAtEnd = Time.frameCount;

                    if (step.CaptureScreenshotAfter && !script.CancelRequested)
                    {
                        yield return new WaitForEndOfFrame();
                        if (!script.CancelRequested) TryCapture(result);
                    }
                    script.Results.Add(result);
                    UpdateLast(script, result);
                    if (!result.Ok && script.StopOnError)
                    {
                        terminal = BridgeScriptStatus.Failed;
                        terminalError = result.Error;
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                terminal = BridgeScriptStatus.Failed;
                terminalError = $"BRIDGE_EXCEPTION: {exception.GetType().Name}: {exception.Message}";
                Debug.LogException(exception);
            }
            finally
            {
                if (script.CancelRequested) { terminal = BridgeScriptStatus.Cancelled; terminalError = script.TerminalError ?? terminalError ?? "CANCELLED"; }
                Finish(script, terminal, terminalError);
                _running = null;
                _runningScript = null;
            }
        }

        private void CompleteStep(BridgeScript script, BridgeStepResult result, bool ok, string error, float durationMs)
        {
            result.Ok = ok;
            result.Error = error;
            result.DurationMs = durationMs;
            result.FrameAtEnd = Time.frameCount;
            script.Results.Add(result);
            UpdateLast(script, result);
        }

        private void Finish(BridgeScript script, BridgeScriptStatus status, string error)
        {
            if (script == null) return;
            script.TrySetTerminal(status, error);
            _scripts.TryRemove(script.Id, out _);
            Last.LastScriptId = script.Id;
            Last.LastScriptStatus = script.Status;
            if (!string.IsNullOrEmpty(error)) Last.LastError = error;
        }

        private void TryCapture(BridgeStepResult result)
        {
            var camera = Camera.main;
            if (camera == null)
            {
                float depth = float.NegativeInfinity;
                foreach (var candidate in Camera.allCameras)
                    if (candidate != null && candidate.enabled && candidate.depth > depth)
                    { camera = candidate; depth = candidate.depth; }
            }
            if (camera == null) { result.Error = result.Error ?? "SCREENSHOT_CAMERA_NOT_FOUND"; return; }

            const int width = 1280;
            const int height = 720;
            RenderTexture texture = null;
            Texture2D image = null;
            var previousActive = RenderTexture.active;
            var previousTarget = camera.targetTexture;
            try
            {
                texture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = texture;
                camera.Render();
                RenderTexture.active = texture;
                image = new Texture2D(width, height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply();
                result.ScreenshotPng = image.EncodeToPNG();
                result.ScreenshotWidth = width;
                result.ScreenshotHeight = height;
            }
            catch (Exception exception)
            {
                result.Error = result.Error ?? $"SCREENSHOT_FAILED: {exception.Message}";
                Debug.LogWarning($"[scene-analyzer] bridge screenshot failed: {exception.Message}");
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (image != null) Destroy(image);
                if (texture != null) RenderTexture.ReleaseTemporary(texture);
            }
        }

        private int TryGetEditorLogSeq()
        {
            try
            {
                var type = Type.GetType("Maicon.SceneAnalyzer.Editor.Core.ConsoleCapture, com.maicon.scene-analyzer.Editor");
                var property = type?.GetProperty("CurrentSeq",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                return property != null ? (int)property.GetValue(null) : 0;
            }
            catch { return 0; }
        }

        private void UpdateLast(BridgeScript script, BridgeStepResult result)
        {
            Last.LastScriptId = script.Id;
            Last.LastStepName = result.Name;
            Last.LastFrameCount = result.FrameAtEnd;
            Last.LastOk = result.Ok;
            Last.LastError = result.Error;
            Last.LastScriptStatus = script.Status;
        }
    }
}
