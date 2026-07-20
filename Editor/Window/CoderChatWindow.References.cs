using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Maicon.SceneAnalyzer.Editor.Chat;
using Maicon.SceneAnalyzer.Editor.Core;
using UnityEditor;
using UnityEditor.Search;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Maicon.SceneAnalyzer.Editor.Window
{
    public sealed partial class CoderChatWindow
    {
        private const int MaxReferenceResults = 240;
        private const int SearchItemsPerBatch = 160;
        private const double SearchBatchBudgetMs = 3.0;

        private readonly List<ReferenceSearchResult> _referenceResults = new();
        private readonly HashSet<string> _referenceResultKeys = new(StringComparer.Ordinal);
        private VisualElement _referencePicker;
        private ListView _referenceResultsList;
        private TextField _referenceSearchField;
        private Label _referenceStatus;
        private Button _sceneTab;
        private Button _assetsTab;
        private IVisualElementScheduledItem _referenceDebounce;
        private IVisualElementScheduledItem _referenceSearchPump;
        private SearchContext _referenceSearchContext;
        private ISearchList _referenceSearchList;
        private IEnumerator<SearchItem> _referenceSearchEnumerator;
        private string _referenceMode = "scene";
        private string _sceneSearchQuery = "";
        private int _referenceSearchGeneration;
        private bool _referenceSearchPending;

        internal static void AttachObjects(IEnumerable<Object> objects)
        {
            var window = GetWindow<CoderChatWindow>(WindowTitle);
            window.minSize = new Vector2(420, 560);
            window.Show();
            window.AddReferences(objects);
        }

        private void OpenReferencePicker()
        {
            if (_referencePicker != null)
            {
                _referenceSearchField?.Focus();
                return;
            }

            BuildReferencePicker();
            rootVisualElement.Add(_referencePicker);
            _referencePicker.BringToFront();
            _referencePicker.schedule.Execute(() =>
            {
                StartReferenceSearch();
                _referenceSearchField?.Focus();
            });
        }

        private void BuildReferencePicker()
        {
            _referencePicker = new VisualElement { name = "reference-picker" };
            _referencePicker.AddToClassList("reference-picker-overlay");

            var panel = new VisualElement();
            panel.AddToClassList("reference-picker-panel");
            _referencePicker.Add(panel);

            var header = new VisualElement();
            header.AddToClassList("reference-picker-header");
            header.Add(new Label("Anexar referência Unity") { name = "reference-picker-title" });
            header.Add(new Button(CloseReferencePicker) { text = "Fechar" });
            panel.Add(header);

            var tabs = new VisualElement();
            tabs.AddToClassList("reference-picker-tabs");
            _sceneTab = new Button(() => SetReferenceMode("scene")) { text = "Cena" };
            _assetsTab = new Button(() => SetReferenceMode("asset")) { text = "Assets e pastas" };
            tabs.Add(_sceneTab);
            tabs.Add(_assetsTab);
            panel.Add(tabs);

            _referenceSearchField = new TextField
            {
                name = "reference-search",
                tooltip = "Busque por nome, caminho ou tipo de Component",
            };
            _referenceSearchField.RegisterValueChangedCallback(_ => ScheduleReferenceSearch());
            _referenceSearchField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Escape) return;
                evt.StopImmediatePropagation();
                CloseReferencePicker();
            });
            panel.Add(_referenceSearchField);

            _referenceStatus = new Label("Preparando busca…");
            _referenceStatus.AddToClassList("reference-picker-status");
            panel.Add(_referenceStatus);

            _referenceResultsList = new ListView
            {
                itemsSource = _referenceResults,
                fixedItemHeight = 44f,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                selectionType = SelectionType.Single,
                showBorder = false,
            };
            _referenceResultsList.AddToClassList("reference-results");
            _referenceResultsList.makeItem = MakeReferenceResultRow;
            _referenceResultsList.bindItem = BindReferenceResultRow;
            _referenceResultsList.itemsChosen += selected =>
            {
                foreach (var value in selected)
                {
                    if (value is ReferenceSearchResult result) AddReferenceResult(result);
                    break;
                }
            };
            panel.Add(_referenceResultsList);

            var hint = new Label("Duplo clique adiciona. Você também pode arrastar objetos do Hierarchy ou Project diretamente para o composer.");
            hint.AddToClassList("reference-picker-status");
            panel.Add(hint);

            _referenceSearchPump = _referencePicker.schedule.Execute(ProcessReferenceSearchBatch).Every(16);
            SetReferenceMode("scene", false);
        }

        private VisualElement MakeReferenceResultRow()
        {
            var row = new VisualElement();
            row.AddToClassList("reference-result-row");

            var copy = new VisualElement();
            copy.AddToClassList("reference-result-copy");
            var label = new Label { name = "result-label" };
            label.AddToClassList("reference-result-label");
            var detail = new Label { name = "result-detail" };
            detail.AddToClassList("reference-result-detail");
            copy.Add(label);
            copy.Add(detail);
            row.Add(copy);

            var add = new Button(() =>
            {
                if (row.userData is ReferenceSearchResult result) AddReferenceResult(result);
            }) { text = "Adicionar" };
            row.Add(add);
            return row;
        }

        private void BindReferenceResultRow(VisualElement row, int index)
        {
            var result = index >= 0 && index < _referenceResults.Count ? _referenceResults[index] : null;
            row.userData = result;
            row.Q<Label>("result-label").text = result?.Label ?? "";
            row.Q<Label>("result-detail").text = result?.Detail ?? "";
            row.SetEnabled(result != null);
        }

        private void ScheduleReferenceSearch()
        {
            _referenceDebounce?.Pause();
            _referenceDebounce = _referencePicker?.schedule.Execute(StartReferenceSearch);
            _referenceDebounce?.ExecuteLater(160);
        }

        private void StartReferenceSearch()
        {
            if (_referencePicker == null) return;
            var generation = ++_referenceSearchGeneration;
            _referenceResults.Clear();
            _referenceResultKeys.Clear();
            _referenceResultsList?.RefreshItems();
            CancelReferenceSearch();

            var query = (_referenceSearchField?.value ?? "").Trim();
            StartProviderSearch(_referenceMode == "asset" ? "asset" : "scene", query, generation);
        }

        private void StartProviderSearch(string providerId, string query, int generation)
        {
            _sceneSearchQuery = providerId == "scene" ? query ?? "" : "";
            var providerQuery = providerId == "scene" ? "" : query ?? "";
            SetReferenceStatus(providerId == "scene" ? "Buscando na cena…" : "Buscando assets e pastas…");

            try
            {
                _referenceSearchContext = SearchService.CreateContext(providerId, providerQuery, SearchFlags.Sorted);
                _referenceSearchList = SearchService.Request(_referenceSearchContext, SearchFlags.Sorted);
                _referenceSearchEnumerator = _referenceSearchList?.GetEnumerator();
                _referenceSearchPending = generation == _referenceSearchGeneration
                    && _referenceSearchEnumerator != null;
                if (!_referenceSearchPending)
                {
                    SetReferenceStatus("A busca não pôde ser iniciada.");
                    CancelReferenceSearch();
                    return;
                }
                ProcessReferenceSearchBatch();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                SetReferenceStatus($"Falha na busca: {exception.Message}");
                CancelReferenceSearch();
            }
        }

        private void ProcessReferenceSearchBatch()
        {
            if (!_referenceSearchPending || _referencePicker == null || _referenceSearchEnumerator == null) return;

            var stopwatch = Stopwatch.StartNew();
            var processed = 0;
            var completed = false;
            try
            {
                while (processed < SearchItemsPerBatch
                       && stopwatch.Elapsed.TotalMilliseconds < SearchBatchBudgetMs
                       && _referenceResults.Count < MaxReferenceResults)
                {
                    if (!_referenceSearchEnumerator.MoveNext())
                    {
                        completed = true;
                        break;
                    }
                    processed++;
                    var item = _referenceSearchEnumerator.Current;
                    if (item != null) AppendSearchItem(item);
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                SetReferenceStatus($"Falha ao enumerar a busca: {exception.Message}");
                completed = true;
            }

            _referenceResultsList?.RefreshItems();
            if (_referenceResults.Count >= MaxReferenceResults || completed)
            {
                var source = _referenceMode == "scene" ? "Cena" : "Assets";
                SetReferenceStatus(ResultStatus(source));
                CancelReferenceSearch();
            }
            else
            {
                var source = _referenceMode == "scene" ? "na cena" : "em assets e pastas";
                SetReferenceStatus($"Buscando {source}… {_referenceResults.Count} resultado(s)");
            }
        }

        private void AppendSearchItem(SearchItem item)
        {
            if (_referenceMode == "asset")
            {
                AddReferenceResultCandidate(new ReferenceSearchResult
                {
                    Key = "asset:" + (item.id ?? item.label ?? Guid.NewGuid().ToString("N")),
                    Label = string.IsNullOrEmpty(item.label) ? item.id : item.label,
                    Detail = string.IsNullOrEmpty(item.description) ? item.id : item.description,
                    SearchItem = item,
                    AssetPath = AssetPathFromSearchItem(item),
                });
                return;
            }

            var target = item.ToObject();
            var gameObject = target as GameObject ?? (target as Component)?.gameObject;
            if (gameObject == null) return;
            var hierarchyPath = BuildHierarchyPath(gameObject.transform);
            if (MatchesSceneObject(gameObject, hierarchyPath, _sceneSearchQuery))
            {
                AddReferenceResultCandidate(new ReferenceSearchResult
                {
                    Key = "scene-go:" + gameObject.GetInstanceID(),
                    Label = gameObject.name,
                    Detail = $"GameObject · {gameObject.scene.name} · {hierarchyPath}",
                    InstanceId = gameObject.GetInstanceID(),
                });
            }

            if (string.IsNullOrWhiteSpace(_sceneSearchQuery)) return;
            foreach (var component in gameObject.GetComponents<Component>())
            {
                if (component == null || !Contains(component.GetType().FullName, _sceneSearchQuery)) continue;
                AddReferenceResultCandidate(new ReferenceSearchResult
                {
                    Key = "scene-component:" + component.GetInstanceID(),
                    Label = component.GetType().Name,
                    Detail = $"Component · {hierarchyPath}",
                    InstanceId = component.GetInstanceID(),
                });
                if (_referenceResults.Count >= MaxReferenceResults) break;
            }
        }

        private static bool MatchesSceneObject(GameObject gameObject, string hierarchyPath, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            return Contains(gameObject.name, query)
                || Contains(hierarchyPath, query)
                || Contains(gameObject.scene.name, query)
                || Contains(gameObject.tag, query);
        }

        private static string BuildHierarchyPath(Transform transform)
        {
            if (transform == null) return "";
            var names = new Stack<string>();
            for (var current = transform; current != null; current = current.parent)
                names.Push(current.name);
            return string.Join("/", names);
        }

        private void AddReferenceResultCandidate(ReferenceSearchResult result)
        {
            if (result == null || _referenceResults.Count >= MaxReferenceResults) return;
            var key = string.IsNullOrEmpty(result.Key) ? result.Label + ":" + result.Detail : result.Key;
            if (!_referenceResultKeys.Add(key)) return;
            _referenceResults.Add(result);
        }

        private void AddReferenceResult(ReferenceSearchResult result)
        {
            if (result == null) return;
            ChatReference reference = null;
            try
            {
                Object target = null;
                if (result.InstanceId != 0) target = EditorUtility.InstanceIDToObject(result.InstanceId);
                if (target == null && result.SearchItem != null) target = result.SearchItem.ToObject();
                reference = target != null
                    ? ChatReference.Capture(target)
                    : ChatReference.FromAssetPath(result.AssetPath);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                ShowComposerFeedback($"Não foi possível anexar '{result.Label}': {exception.Message}");
            }

            if (reference != null) AddReference(reference);
        }

        private void SetReferenceMode(string mode, bool restart = true)
        {
            _referenceMode = mode == "asset" ? "asset" : "scene";
            _sceneTab?.EnableInClassList("selected", _referenceMode == "scene");
            _assetsTab?.EnableInClassList("selected", _referenceMode == "asset");
            if (restart) StartReferenceSearch();
        }

        private void CloseReferencePicker()
        {
            _referenceSearchGeneration++;
            _referenceDebounce?.Pause();
            _referenceDebounce = null;
            _referenceSearchPump?.Pause();
            _referenceSearchPump = null;
            CancelReferenceSearch();
            _referencePicker?.RemoveFromHierarchy();
            _referencePicker = null;
            _referenceResultsList = null;
            _referenceSearchField = null;
            _referenceStatus = null;
            _composerInput?.Focus();
        }

        private void CancelReferenceSearch()
        {
            _referenceSearchPending = false;
            try { _referenceSearchEnumerator?.Dispose(); }
            catch (Exception exception) { Debug.LogException(exception); }
            _referenceSearchEnumerator = null;
            _referenceSearchList = null;

            var context = _referenceSearchContext;
            _referenceSearchContext = null;
            if (context == null) return;
            try { context.Dispose(); }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        private void AttachCurrentSelection()
        {
            var selected = Selection.objects;
            if (selected == null || selected.Length == 0)
            {
                ShowComposerFeedback("Selecione um objeto no Hierarchy, Inspector ou Project.");
                return;
            }
            AddReferences(selected);
        }

        private void AddReferences(IEnumerable<Object> objects)
        {
            if (objects == null) return;
            var added = 0;
            foreach (var target in objects)
            {
                var reference = ChatReference.Capture(target);
                if (reference == null) continue;
                if (AddReference(reference)) added++;
            }

            if (added == 0)
                ShowComposerFeedback("Nenhuma referência nova pôde ser adicionada.");
            else
                HideComposerFeedback();
        }

        private bool AddReference(ChatReference reference)
        {
            if (reference == null) return false;
            reference.Validate();
            if (_draftReferences.Any(existing => existing != null
                && string.Equals(existing.CanonicalKey, reference.CanonicalKey, StringComparison.Ordinal)))
                return false;
            _draftReferences.Add(reference);
            RenderReferenceChips();
            return true;
        }

        private void RenderReferenceChips()
        {
            if (_referenceChips == null) return;
            _referenceChips.Clear();
            foreach (var reference in _draftReferences.ToArray())
            {
                if (reference == null) continue;
                reference.Validate();
                var chip = new VisualElement();
                chip.AddToClassList("reference-chip");
                if (!reference.IsAvailable) chip.AddToClassList("unavailable");
                chip.tooltip = reference.IsAvailable
                    ? $"{reference.Kind} · {reference.CanonicalKey}"
                    : reference.ValidationError ?? "Referência indisponível";

                var locate = new Button(() => PingDraftReference(reference))
                {
                    text = string.IsNullOrEmpty(reference.Label) ? reference.Kind : reference.Label,
                };
                chip.Add(locate);
                var remove = new Button(() =>
                {
                    _draftReferences.Remove(reference);
                    RenderReferenceChips();
                }) { text = "×", tooltip = "Remover referência" };
                remove.AddToClassList("reference-remove-button");
                chip.Add(remove);
                _referenceChips.Add(chip);
            }
            SetDisplayed(_referenceChips, _draftReferences.Count > 0);
        }

        private void PingDraftReference(ChatReference reference)
        {
            if (reference?.Target == null) return;
            var target = ToolTargetResolver.Resolve(reference.Target, out var error);
            reference.IsAvailable = target != null;
            reference.ValidationError = error;
            if (target == null)
            {
                RenderReferenceChips();
                ShowComposerFeedback(error ?? "Referência indisponível.");
                return;
            }
            Selection.activeObject = target;
            EditorGUIUtility.PingObject(target);
        }

        private void RegisterComposerDragAndDrop()
        {
            var panel = rootVisualElement.Q<VisualElement>("composer-panel");
            if (panel == null) return;
            panel.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                if (DragAndDrop.objectReferences == null || DragAndDrop.objectReferences.Length == 0) return;
                DragAndDrop.visualMode = DragAndDropVisualMode.Link;
                evt.StopPropagation();
            });
            panel.RegisterCallback<DragPerformEvent>(evt =>
            {
                if (DragAndDrop.objectReferences == null || DragAndDrop.objectReferences.Length == 0) return;
                DragAndDrop.AcceptDrag();
                AddReferences(DragAndDrop.objectReferences);
                evt.StopPropagation();
            });
        }

        private string ResultStatus(string source)
        {
            var suffix = _referenceResults.Count >= MaxReferenceResults ? $" · limite de {MaxReferenceResults}" : "";
            return $"{source}: {_referenceResults.Count} resultado(s){suffix}";
        }

        private void SetReferenceStatus(string text)
        {
            if (_referenceStatus != null) _referenceStatus.text = text ?? "";
        }

        private static string AssetPathFromSearchItem(SearchItem item)
        {
            if (item == null) return null;
            foreach (var candidate in new[] { item.id, item.description })
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                var normalized = candidate.Replace('\\', '/');
                var assetsIndex = normalized.IndexOf("Assets/", StringComparison.Ordinal);
                if (assetsIndex >= 0) return normalized.Substring(assetsIndex);
                var packagesIndex = normalized.IndexOf("Packages/", StringComparison.Ordinal);
                if (packagesIndex >= 0) return normalized.Substring(packagesIndex);
            }
            return null;
        }

        private static bool Contains(string value, string query)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(query ?? "", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ShortTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return "Component";
            var index = Math.Max(typeName.LastIndexOf('.'), typeName.LastIndexOf('+'));
            return index >= 0 && index + 1 < typeName.Length ? typeName.Substring(index + 1) : typeName;
        }

        private sealed class ReferenceSearchResult
        {
            public string Key;
            public string Label;
            public string Detail;
            public int InstanceId;
            public string AssetPath;
            public SearchItem SearchItem;
        }
    }
}
