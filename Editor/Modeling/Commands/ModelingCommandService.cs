using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Maicon.SceneAnalyzer.Editor.Modeling.Core;
using Maicon.SceneAnalyzer.Editor.Modeling.Validation;
using UnityEditor;
using UnityEngine;

namespace Maicon.SceneAnalyzer.Editor.Modeling.Commands
{
    public interface IRecordedMeshOperation : IMeshOperation
    {
        string SerializeParameters();
    }

    public sealed class MeshOperationPreview : IDisposable
    {
        public Mesh PreviewMesh { get; internal set; }
        public MeshOperationResult Result { get; internal set; }
        public string PreviewHash => Result?.ResultHash ?? string.Empty;

        public void Dispose()
        {
            if (PreviewMesh != null) UnityEngine.Object.DestroyImmediate(PreviewMesh);
            PreviewMesh = null;
        }
    }

    public static class MeshOperationRegistry
    {
        private static readonly Dictionary<string, Func<string, IMeshOperation>> Factories =
            new Dictionary<string, Func<string, IMeshOperation>>(StringComparer.Ordinal);

        public static void Register(string operationId, Func<string, IMeshOperation> factory)
        {
            if (string.IsNullOrWhiteSpace(operationId)) throw new ArgumentException("Operation id is required.", nameof(operationId));
            Factories[operationId] = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public static bool TryCreate(string operationId, string parametersJson, out IMeshOperation operation)
        {
            operation = null;
            return !string.IsNullOrEmpty(operationId)
                && Factories.TryGetValue(operationId, out var factory)
                && (operation = factory(parametersJson ?? "{}")) != null;
        }

        public static IReadOnlyCollection<string> RegisteredOperationIds => Factories.Keys;
    }

    public static class ModelingCommandService
    {
        public static MeshOperationPreview Preview(
            MeshEditDocument document,
            IMeshOperation operation,
            MeshRevision expectedRevision,
            CancellationToken cancellationToken = default)
        {
            EnsureDocument(document, expectedRevision);
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            cancellationToken.ThrowIfCancellationRequested();

            var previewMesh = MeshChannelSnapshot.Capture(document.WorkingMesh)
                .CreateMesh(document.WorkingMesh.name + "_Preview");
            try
            {
                var context = new MeshOperationContext(document, previewMesh, expectedRevision, cancellationToken);
                var validation = operation.Validate(context) ?? new MeshValidationReport();
                if (!validation.IsValid)
                    throw new InvalidOperationException("OPERATION_VALIDATION_FAILED: " + Summarize(validation));

                var stopwatch = Stopwatch.StartNew();
                var result = operation.Apply(context) ?? new MeshOperationResult();
                stopwatch.Stop();
                cancellationToken.ThrowIfCancellationRequested();

                var meshValidation = MeshValidator.Validate(previewMesh);
                Merge(validation, meshValidation);
                result.OperationId = operation.Descriptor.Id;
                result.RevisionBefore = expectedRevision;
                result.RevisionAfter = operation.Descriptor.ChangesTopology
                    ? expectedRevision.NextTopology()
                    : expectedRevision.NextGeometry();
                result.InputHash = MeshFingerprint.Compute(document.WorkingMesh);
                result.ResultHash = MeshFingerprint.Compute(previewMesh);
                result.Validation = validation;
                if (!validation.IsValid)
                    throw new InvalidOperationException("OPERATION_RESULT_INVALID: " + Summarize(validation));
                return new MeshOperationPreview { PreviewMesh = previewMesh, Result = result };
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(previewMesh);
                throw;
            }
        }

        public static MeshOperationResult Apply(
            MeshEditDocument document,
            IMeshOperation operation,
            MeshRevision expectedRevision,
            string approvedPreviewHash,
            CancellationToken cancellationToken = default)
        {
            using (var preview = Preview(document, operation, expectedRevision, cancellationToken))
            {
                if ((operation.Descriptor.RequiresPreview || operation.Descriptor.ChangesTopology)
                    && string.IsNullOrWhiteSpace(approvedPreviewHash))
                    throw new InvalidOperationException("PREVIEW_APPROVAL_REQUIRED: previewHash is required for this operation.");
                if (!string.IsNullOrWhiteSpace(approvedPreviewHash)
                    && !string.Equals(approvedPreviewHash, preview.PreviewHash, StringComparison.Ordinal))
                    throw new InvalidOperationException("STALE_PREVIEW_HASH: operation preview differs from the approved preview.");

                Undo.RegisterCompleteObjectUndo(
                    new UnityEngine.Object[] { document.WorkingMesh, document },
                    operation.Descriptor.DisplayName);
                MeshChannelSnapshot.Capture(preview.PreviewMesh).ApplyAll(document.WorkingMesh);
                if (operation.Descriptor.ChangesTopology)
                    document.AdvanceTopology(PreservesOriginalVertexIndices(operation.Descriptor.Id));
                else document.AdvanceGeometry();

                var parameters = operation is IRecordedMeshOperation recorded
                    ? recorded.SerializeParameters()
                    : "{}";
                document.AddModifier(new MeshModifierRecord(
                    operation.Descriptor.Id,
                    operation.Descriptor.DisplayName,
                    parameters,
                    preview.Result.InputHash,
                    preview.Result.ResultHash,
                    preview.Result.RevisionBefore,
                    document.Revision));
                EditorUtility.SetDirty(document.WorkingMesh);
                AssetDatabase.SaveAssets();
                preview.Result.RevisionAfter = document.Revision;
                return preview.Result;
            }
        }

        public static MeshValidationReport Rebuild(MeshEditDocument document, bool includeDisabled = false)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (document.SourceMesh == null) throw new InvalidOperationException("SOURCE_MISSING: source mesh is unavailable.");
            if (document.HasSourceDrift())
            {
                document.SetState(MeshEditDocumentState.SourceDrifted);
                throw new InvalidOperationException("SOURCE_DRIFTED: rebase the session before rebuilding.");
            }

            var rebuilt = MeshChannelSnapshot.Capture(document.SourceMesh)
                .CreateMesh(document.WorkingMesh.name + "_Rebuild");
            try
            {
                for (int i = 0; i < document.ModifierStack.Count; i++)
                {
                    var record = document.ModifierStack[i];
                    if (!includeDisabled && !record.Enabled) continue;
                    if (!MeshOperationRegistry.TryCreate(record.OperationId, record.ParametersJson, out var operation))
                        throw new InvalidOperationException($"OPERATION_NOT_REGISTERED: '{record.OperationId}'.");
                    var context = new MeshOperationContext(document, rebuilt, document.Revision, CancellationToken.None);
                    var validation = operation.Validate(context);
                    if (validation != null && !validation.IsValid)
                        throw new InvalidOperationException("OPERATION_VALIDATION_FAILED: " + Summarize(validation));
                    operation.Apply(context);
                }

                var report = MeshValidator.Validate(rebuilt);
                if (!report.IsValid) return report;
                Undo.RegisterCompleteObjectUndo(document.WorkingMesh, "Rebuild editable mesh");
                MeshChannelSnapshot.Capture(rebuilt).ApplyAll(document.WorkingMesh);
                document.AdvanceTopology();
                EditorUtility.SetDirty(document.WorkingMesh);
                AssetDatabase.SaveAssets();
                return report;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rebuilt);
            }
        }

        public static MeshValidationReport Commit(MeshEditDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            EnsureDocument(document, document.Revision);
            var report = MeshValidator.Validate(document.WorkingMesh);
            if (!report.IsValid) return report;
            document.SetState(MeshEditDocumentState.Committed);
            EditorUtility.SetDirty(document.WorkingMesh);
            AssetDatabase.SaveAssets();
            return report;
        }

        public static MeshValidationReport ReorderModifier(
            MeshEditDocument document,
            int fromIndex,
            int toIndex,
            MeshRevision expectedRevision)
        {
            EnsureDocument(document, expectedRevision);
            Undo.RegisterCompleteObjectUndo(
                new UnityEngine.Object[] { document.WorkingMesh, document },
                "Reorder mesh modifier");
            document.MoveModifier(fromIndex, toIndex);
            try { return Rebuild(document); }
            catch
            {
                document.MoveModifier(toIndex, fromIndex);
                throw;
            }
        }

        public static MeshValidationReport RemoveModifier(
            MeshEditDocument document,
            int index,
            MeshRevision expectedRevision)
        {
            EnsureDocument(document, expectedRevision);
            if (index < 0 || index >= document.ModifierStack.Count) throw new ArgumentOutOfRangeException(nameof(index));
            var record = document.ModifierStack[index];
            Undo.RegisterCompleteObjectUndo(
                new UnityEngine.Object[] { document.WorkingMesh, document },
                "Remove mesh modifier");
            document.RemoveModifierAt(index);
            try { return Rebuild(document); }
            catch
            {
                document.InsertModifierAt(index, record);
                throw;
            }
        }

        public static MeshValidationReport SetModifierEnabled(
            MeshEditDocument document,
            int index,
            bool enabled,
            MeshRevision expectedRevision)
        {
            EnsureDocument(document, expectedRevision);
            if (index < 0 || index >= document.ModifierStack.Count) throw new ArgumentOutOfRangeException(nameof(index));
            bool previous = document.ModifierStack[index].Enabled;
            Undo.RegisterCompleteObjectUndo(
                new UnityEngine.Object[] { document.WorkingMesh, document },
                enabled ? "Enable mesh modifier" : "Disable mesh modifier");
            document.SetModifierEnabled(index, enabled);
            try { return Rebuild(document); }
            catch
            {
                document.SetModifierEnabled(index, previous);
                throw;
            }
        }

        public static void Rebase(MeshEditDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (document.SourceMesh == null) throw new InvalidOperationException("SOURCE_MISSING: source mesh is unavailable.");
            Undo.RegisterCompleteObjectUndo(
                new UnityEngine.Object[] { document.WorkingMesh, document },
                "Rebase editable mesh");
            MeshChannelSnapshot.Capture(document.SourceMesh).ApplyAll(document.WorkingMesh);
            document.RebaseToSource();
            EditorUtility.SetDirty(document.WorkingMesh);
            AssetDatabase.SaveAssets();
        }

        public static void Discard(MeshEditDocument document)
        {
            Rebase(document);
            document.SetState(MeshEditDocumentState.Discarded);
            AssetDatabase.SaveAssets();
        }

        private static void EnsureDocument(MeshEditDocument document, MeshRevision expectedRevision)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (document.WorkingMesh == null) throw new InvalidOperationException("WORKING_MESH_MISSING: document has no working mesh.");
            if (!document.Matches(expectedRevision))
                throw new InvalidOperationException($"STALE_MESH_REVISION: expected {expectedRevision}, current {document.Revision}.");
            if (document.HasSourceDrift())
            {
                document.SetState(MeshEditDocumentState.SourceDrifted);
                throw new InvalidOperationException("SOURCE_DRIFTED: source mesh changed after the session began.");
            }
            if (document.State == MeshEditDocumentState.Invalid || document.State == MeshEditDocumentState.Discarded)
                throw new InvalidOperationException($"SESSION_NOT_ACTIVE: document state is {document.State}.");
        }

        private static void Merge(MeshValidationReport target, MeshValidationReport source)
        {
            if (source == null) return;
            for (int i = 0; i < source.Issues.Count; i++)
            {
                var issue = source.Issues[i];
                if (issue.IsError) target.Error(issue.Code, issue.Message);
                else target.Warning(issue.Code, issue.Message);
            }
        }

        private static bool PreservesOriginalVertexIndices(string operationId)
        {
            switch (operationId)
            {
                case "mesh.mirror":
                case "mesh.subdivide":
                case "mesh.extrude_faces":
                case "mesh.extrude_edges":
                case "mesh.bevel_faces":
                case "mesh.bridge_loops":
                case "mesh.fill_hole":
                case "mesh.adaptive_refine":
                case "mesh.surface_fit":
                    return true;
                default:
                    return false;
            }
        }

        private static string Summarize(MeshValidationReport report)
        {
            if (report == null || report.Issues.Count == 0) return "unknown validation error";
            var count = Math.Min(5, report.Issues.Count);
            var messages = new string[count];
            for (int i = 0; i < count; i++)
                messages[i] = report.Issues[i].Code + ": " + report.Issues[i].Message;
            return string.Join(" | ", messages);
        }
    }
}
