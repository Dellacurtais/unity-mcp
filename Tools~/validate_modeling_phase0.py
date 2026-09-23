from __future__ import annotations

import json
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def require(condition: bool, message: str, failures: list[str]) -> None:
    if not condition:
        failures.append(message)


def read(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8")


def main() -> int:
    failures: list[str] = []
    required = (
        "Documentation~/asset-modeler-complete-spec.md",
        "Runtime/Modeling/com.maicon.scene-analyzer.Runtime.Modeling.asmdef",
        "Runtime/Modeling/BodyPartSet.cs",
        "Runtime/Modeling/ModularCharacterAssembler.cs",
        "Editor/Modeling/com.maicon.scene-analyzer.Editor.Modeling.asmdef",
        "Editor/Modeling/Core/ModelingContracts.cs",
        "Editor/Modeling/Core/MeshChannelSnapshot.cs",
        "Editor/Modeling/Core/MeshEditDocument.cs",
        "Editor/Modeling/SmartSplit/SmartBodySplitAnalyzer.cs",
        "Editor/Modeling/SmartSplit/SmartBodySplitService.cs",
        "Editor/Modeling/SmartSplit/SplitFidelityValidator.cs",
        "Editor/Modeling/Mcp/SmartBodySplitTools.cs",
        "Tests/Editor/Modeling/com.maicon.scene-analyzer.Editor.Modeling.Tests.asmdef",
        "Tests/Editor/Modeling/ModelingPhaseZeroTests.cs",
        "Tests/Editor/Modeling/ModelingBenchmarkTests.cs",
    )
    for relative in required:
        require((ROOT / relative).is_file(), f"required Phase 0 file is missing: {relative}", failures)

    if failures:
        for failure in failures:
            print(f"FAIL: {failure}")
        return 1

    runtime_asmdef = json.loads(read("Runtime/Modeling/com.maicon.scene-analyzer.Runtime.Modeling.asmdef"))
    editor_asmdef = json.loads(read("Editor/Modeling/com.maicon.scene-analyzer.Editor.Modeling.asmdef"))
    tests_asmdef = json.loads(read("Tests/Editor/Modeling/com.maicon.scene-analyzer.Editor.Modeling.Tests.asmdef"))
    require(
        runtime_asmdef.get("name") == "com.maicon.scene-analyzer.Runtime.Modeling",
        "runtime modeling asmdef name drifted",
        failures,
    )
    require(
        editor_asmdef.get("name") == "com.maicon.scene-analyzer.Editor.Modeling",
        "editor modeling asmdef name drifted",
        failures,
    )
    require(
        "com.maicon.scene-analyzer.Runtime.Modeling" in editor_asmdef.get("references", []),
        "editor modeling assembly must reference the runtime modeling contracts",
        failures,
    )
    require(
        tests_asmdef.get("name") == "com.maicon.scene-analyzer.Editor.Modeling.Tests",
        "modeling test asmdef name drifted",
        failures,
    )
    require(
        "com.maicon.scene-analyzer.Editor.Modeling" in tests_asmdef.get("references", []),
        "modeling tests must reference the editor modeling assembly",
        failures,
    )

    for directory in (ROOT / "Editor/Modeling", ROOT / "Runtime/Modeling", ROOT / "Tests/Editor/Modeling"):
        for path in directory.rglob("*"):
            if path.suffix not in {".cs", ".asmdef"}:
                continue
            require(
                Path(str(path) + ".meta").is_file(),
                f"Unity metadata is missing: {path.relative_to(ROOT)}.meta",
                failures,
            )

    snapshot = read("Editor/Modeling/Core/MeshChannelSnapshot.cs")
    for token in (
        "GetAllBoneWeights",
        "GetBonesPerVertex",
        "GetBlendShapeFrameVertices",
        "AddBlendShapeFrame",
        "new MeshUvSnapshot[8]",
        "GetVertexData<byte>",
        "SetVertexBufferData",
        "source.BaseVertex",
    ):
        require(token in snapshot, f"full channel snapshot contract lost token: {token}", failures)

    tools = read("Editor/Modeling/Mcp/SmartBodySplitTools.cs")
    for name in (
        "mesh_analyze_body_parts",
        "mesh_preview_smart_split",
        "mesh_apply_smart_split",
        "mesh_validate_split",
    ):
        require(name in tools, f"Smart Body Split MCP tool is missing: {name}", failures)
    require("hands_feet" in tools, "Smart Body Split detailed preset must be exposed through MCP", failures)
    require("McpCapability.AssetWrite" in tools, "split apply must declare AssetWrite", failures)
    require("McpCapability.Destructive" in tools, "split apply must declare Destructive", failures)
    require("McpConfirmationPolicy.Required" in tools, "split apply must require confirmation", failures)
    require("expectedSourceHash" in tools and "previewHash" in tools, "split apply must be hash-gated", failures)

    service = read("Editor/Modeling/SmartSplit/SmartBodySplitService.cs")
    require("SplitFidelityValidator.Validate" in service, "split must validate before commit", failures)
    require("STALE_SOURCE_HASH" in service, "split must reject stale source state", failures)
    require("STALE_PREVIEW_HASH" in service, "split must reject stale preview state", failures)
    require("STALE_SOURCE_BINDING" in service, "split must reject renderer mesh swaps", failures)
    require("PREVIEW_APPROVAL_REQUIRED" in service, "split must require an approved preview", failures)

    analyzer = read("Editor/Modeling/SmartSplit/SmartBodySplitAnalyzer.cs")
    require("MESH_NOT_READABLE" in analyzer, "non-readable imported meshes need an actionable error", failures)

    tests = read("Tests/Editor/Modeling/ModelingPhaseZeroTests.cs")
    for test_name in (
        "Analyzer_GenericRigProducesSixCompleteParts",
        "Analyzer_HandsAndFeetPresetProducesTenPartsAndKeepsCoreCompatible",
        "Split_HandsAndFeetPresetCreatesTenValidatedRiggedParts",
        "BoneWeightValidation_ToleratesQuantizationAndOrderingButRejectsRealDrift",
        "Split_CreatesValidatedPartsAndLeavesSourceUntouched",
        "Split_RejectsStaleSourceAndPreviewHashesWithoutCreatingAssets",
        "Split_RequiresApprovalAndRejectsRendererMeshSwap",
        "AssetPaths_RejectTraversalSegments",
        "SmartSplitMcpTools_RegisterValidContractsAndExplicitRisk",
    ):
        require(test_name in tests, f"required regression test is missing: {test_name}", failures)

    if failures:
        for failure in failures:
            print(f"FAIL: {failure}")
        return 1

    print("Asset Modeler Phase 0 static gate passed.")
    print("- assemblies: runtime/editor/tests")
    print("- channels: UV0-UV7, rig weights, bind poses, blend shapes")
    print("- Smart Body Split: analyze/preview/apply/validate with hash and confirmation gates")
    return 0


if __name__ == "__main__":
    sys.exit(main())
