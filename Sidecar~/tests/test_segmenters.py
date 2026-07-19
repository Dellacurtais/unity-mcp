import sys
from pathlib import Path

import numpy as np
import trimesh

SIDECAR_ROOT = Path(__file__).resolve().parents[1]
if str(SIDECAR_ROOT) not in sys.path:
    sys.path.insert(0, str(SIDECAR_ROOT))

from segmenters import (  # noqa: E402
    segment_normal_cluster,
    segment_ransac_planes,
    segment_region_growing,
)
from semantic import _orientation, label_planes  # noqa: E402


def _assert_region_contract(region: dict) -> None:
    assert region["region_id"]
    assert region["face_count"] == len(region["face_indices"])
    assert region["face_count"] > 0
    assert len(region["avg_normal"]) == 3
    assert np.isclose(np.linalg.norm(region["avg_normal"]), 1.0)
    assert 0.0 <= region["confidence"] <= 1.0
    assert set(region["world_bounds"]) == {"min", "max"}
    assert len(region["world_bounds"]["min"]) == 3
    assert len(region["world_bounds"]["max"]) == 3


def test_normal_cluster_is_deterministic_and_covers_box_faces() -> None:
    mesh = trimesh.creation.box(extents=(2.0, 1.0, 3.0))
    first = segment_normal_cluster(mesh, buckets=24, min_face_count=1)
    second = segment_normal_cluster(mesh, buckets=24, min_face_count=1)

    assert first == second
    assert sum(region["face_count"] for region in first) == len(mesh.faces)
    for region in first:
        _assert_region_contract(region)


def test_ransac_planes_is_seed_deterministic_and_disjoint() -> None:
    mesh = trimesh.creation.box(extents=(2.0, 1.0, 3.0))
    kwargs = dict(
        angle_tol_deg=5.0,
        distance_tol=0.001,
        max_planes=6,
        min_face_count=2,
        iterations_per_plane=64,
        random_seed=7,
    )
    first = segment_ransac_planes(mesh, **kwargs)
    second = segment_ransac_planes(mesh, **kwargs)

    assert first == second
    assert len(first) == 6
    all_faces = [face for region in first for face in region["face_indices"]]
    assert len(all_faces) == len(set(all_faces)) == len(mesh.faces)
    for region in first:
        _assert_region_contract(region)
        assert region["classification"] == "plane"
        assert "_plane_area" in region
        assert "_plane_point" in region


def test_region_growing_splits_box_into_six_smooth_patches() -> None:
    mesh = trimesh.creation.box(extents=(2.0, 1.0, 3.0))
    regions = segment_region_growing(mesh, frontier_deg=1.0, min_face_count=2)

    assert len(regions) == 6
    assert sorted(region["face_count"] for region in regions) == [2, 2, 2, 2, 2, 2]
    assert sum(region["face_count"] for region in regions) == len(mesh.faces)
    for region in regions:
        _assert_region_contract(region)
        assert region["classification"] == "smooth_patch"


def test_semantic_labelling_identifies_ground_and_strips_internal_fields() -> None:
    mesh = trimesh.creation.box(extents=(10.0, 0.1, 10.0))
    planes = [
        {
            "region_id": "ground",
            "face_indices": [0, 1],
            "face_count": 2,
            "avg_normal": [0.0, 1.0, 0.0],
            "classification": "plane",
            "confidence": 1.0,
            "world_bounds": {"min": [-5.0, -0.05, -5.0], "max": [5.0, -0.05, 5.0]},
            "_plane_area": 100.0,
            "_plane_point": [0.0, -0.05, 0.0],
        },
        {
            "region_id": "ceiling",
            "face_indices": [2, 3],
            "face_count": 2,
            "avg_normal": [0.0, -1.0, 0.0],
            "classification": "plane",
            "confidence": 1.0,
            "world_bounds": {"min": [-5.0, 2.95, -5.0], "max": [5.0, 2.95, 5.0]},
            "_plane_area": 100.0,
            "_plane_point": [0.0, 2.95, 0.0],
        },
    ]

    labelled = label_planes(mesh, planes)

    assert labelled[0]["classification"] == "ground"
    assert labelled[1]["classification"] == "ceiling"
    for region in labelled:
        assert "_plane_area" not in region
        assert "_plane_point" not in region


def test_orientation_uses_unity_y_up_and_normalized_bearing() -> None:
    assert _orientation(np.array([0.0, 1.0, 0.0])) == ("horizontal_up", 0.0)
    assert _orientation(np.array([0.0, -1.0, 0.0])) == ("horizontal_down", 0.0)
    orientation, bearing = _orientation(np.array([0.0, 0.0, -1.0]))
    assert orientation == "vertical:270"
    assert bearing == 270.0
