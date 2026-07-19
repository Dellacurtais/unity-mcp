"""
Mesh segmentation algorithms used by main.py. Pure numpy + trimesh —
no heavy ML deps. Each function returns a list of dicts shaped like the
`SegmentRegion` Pydantic model in main.py (region_id, face_indices,
face_count, avg_normal, classification, confidence, world_bounds).
"""
from __future__ import annotations

from typing import Any

import numpy as np
import trimesh


# ----- helpers -----


def _region_dict(
    region_id: str,
    face_indices: np.ndarray,
    face_normals: np.ndarray,
    mesh: trimesh.Trimesh,
    classification: str,
    confidence: float = 1.0,
) -> dict[str, Any]:
    """Build a regions[i] dict ready for the FastAPI response."""
    avg_n = face_normals[face_indices].mean(axis=0)
    norm = float(np.linalg.norm(avg_n)) or 1.0
    avg_n = (avg_n / norm).tolist()

    # World bounds — assume mesh vertices are already in world space.
    # Caller is responsible for that (the C# side sends world-space OBJ).
    verts = mesh.vertices[mesh.faces[face_indices].reshape(-1)]
    mn = verts.min(axis=0).tolist()
    mx = verts.max(axis=0).tolist()

    return {
        "region_id": region_id,
        "face_indices": face_indices.tolist(),
        "face_count": int(face_indices.size),
        "avg_normal": avg_n,
        "classification": classification,
        "confidence": float(confidence),
        "world_bounds": {"min": mn, "max": mx},
    }


def _face_normals(mesh: trimesh.Trimesh) -> np.ndarray:
    n = mesh.face_normals.astype(np.float64)
    # trimesh may return un-normalised normals for degenerate faces; normalise.
    lens = np.linalg.norm(n, axis=1, keepdims=True)
    lens[lens < 1e-12] = 1.0
    return n / lens


# ----- normal_cluster (baseline) -----


def segment_normal_cluster(
    mesh: trimesh.Trimesh, buckets: int, min_face_count: int
) -> list[dict[str, Any]]:
    """Quantise each face normal onto a Fibonacci-lattice direction sample.
    Cheap baseline; not semantic, but consistent and useful for sanity
    checks (one bucket per dominant wall direction etc.)."""
    normals = _face_normals(mesh)
    n = max(2, buckets)

    indices = np.arange(0, n, dtype=np.float64) + 0.5
    phi = np.arccos(1 - 2 * indices / n)
    theta = np.pi * (1 + 5**0.5) * indices
    dirs = np.stack(
        [
            np.cos(theta) * np.sin(phi),
            np.cos(phi),
            np.sin(theta) * np.sin(phi),
        ],
        axis=1,
    )

    bucket = np.argmax(normals @ dirs.T, axis=1)

    out: list[dict[str, Any]] = []
    for b in range(n):
        idx = np.where(bucket == b)[0]
        if idx.size < min_face_count:
            continue
        out.append(_region_dict(
            region_id=f"sidecar/normal_cluster/{b}",
            face_indices=idx,
            face_normals=normals,
            mesh=mesh,
            classification=f"cluster_{b}",
        ))
    return out


# ----- ransac_planes -----


def segment_ransac_planes(
    mesh: trimesh.Trimesh,
    angle_tol_deg: float,
    distance_tol: float,
    max_planes: int,
    min_face_count: int,
    iterations_per_plane: int = 64,
    random_seed: int = 12345,
) -> list[dict[str, Any]]:
    """Iterative single-seed RANSAC over triangle centroids + normals. Each
    round samples N unassigned faces, picks the seed whose plane attracts
    the most inliers under (angle, distance) tolerance, commits those
    faces, and repeats until the next-best plane is too small or we hit
    `max_planes`.

    Identical mathematics to the C# `PlaneDetector` but with more
    iterations and a more permissive default distance tolerance, both
    feasible thanks to numpy vectorisation."""
    normals = _face_normals(mesh)
    tri_count = mesh.faces.shape[0]
    centroids = mesh.triangles_center.astype(np.float64)

    # Auto distance tolerance: 1% of bounds diagonal.
    if distance_tol < 0:
        diag = float(np.linalg.norm(mesh.bounds[1] - mesh.bounds[0]))
        distance_tol = max(0.001, diag * 0.01)

    cos_angle_tol = float(np.cos(np.radians(angle_tol_deg)))

    assigned = np.zeros(tri_count, dtype=bool)
    rng = np.random.default_rng(random_seed)
    out: list[dict[str, Any]] = []

    while len(out) < max_planes:
        unassigned = np.where(~assigned)[0]
        if unassigned.size < min_face_count:
            break

        best_inliers: np.ndarray | None = None
        best_normal = np.array([0.0, 1.0, 0.0])
        best_point = np.array([0.0, 0.0, 0.0])

        # Sample iterations_per_plane seeds; for each, find inliers in O(F).
        sample_count = min(iterations_per_plane, unassigned.size)
        seeds = rng.choice(unassigned, size=sample_count, replace=False)
        for seed in seeds:
            n = normals[seed]
            p = centroids[seed]
            # Inliers: angle ≤ tol AND distance to plane ≤ tol.
            ang = normals[unassigned] @ n
            ang_ok = ang >= cos_angle_tol
            dist = np.abs((centroids[unassigned] - p) @ n)
            inliers_mask = ang_ok & (dist <= distance_tol)
            inlier_idx = unassigned[inliers_mask]
            if best_inliers is None or inlier_idx.size > best_inliers.size:
                best_inliers = inlier_idx
                best_normal = n
                best_point = p

        if best_inliers is None or best_inliers.size < min_face_count:
            break

        # Commit. Recompute the plane normal as the area-weighted mean for
        # a more stable result (the seed normal is only one face).
        areas = mesh.area_faces[best_inliers]
        weighted_n = (normals[best_inliers] * areas[:, None]).sum(axis=0)
        nn = float(np.linalg.norm(weighted_n)) or 1.0
        plane_normal = weighted_n / nn

        # Centroid of inliers as a representative point.
        weighted_c = (centroids[best_inliers] * areas[:, None]).sum(axis=0) / max(areas.sum(), 1e-9)

        assigned[best_inliers] = True
        out.append(_region_dict(
            region_id=f"sidecar/ransac_plane/{len(out)}",
            face_indices=best_inliers,
            face_normals=normals,
            mesh=mesh,
            classification="plane",
        ))
        # Override the avg_normal / classification with the area-weighted
        # normal we just computed (more accurate than mean-of-mean).
        out[-1]["avg_normal"] = plane_normal.tolist()
        out[-1]["_plane_point"] = weighted_c.tolist()       # used by semantic labeller
        out[-1]["_plane_area"] = float(areas.sum())

    return out


# ----- region_growing -----


def segment_region_growing(
    mesh: trimesh.Trimesh,
    frontier_deg: float,
    min_face_count: int,
) -> list[dict[str, Any]]:
    """Flood-fill across face adjacency where the dihedral angle is below
    `frontier_deg`. The complement of sharp-edge detection: gives you
    smooth patches separated by creases. Useful for organic surfaces and
    softly-curved props."""
    normals = _face_normals(mesh)
    tri_count = mesh.faces.shape[0]

    # face_adjacency is a (N, 2) array of face index pairs sharing an edge.
    adj = mesh.face_adjacency           # (E, 2)
    if adj.size == 0:
        return []

    cos_thresh = float(np.cos(np.radians(frontier_deg)))
    # Edge-wise smoothness flag: True when the two faces are smooth-joined.
    cos_pairs = (normals[adj[:, 0]] * normals[adj[:, 1]]).sum(axis=1)
    smooth_edges = adj[cos_pairs >= cos_thresh]

    # Union-Find on faces using the smooth edges only.
    parent = np.arange(tri_count, dtype=np.int64)

    def find(x: int) -> int:
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    for a, b in smooth_edges:
        ra, rb = find(int(a)), find(int(b))
        if ra != rb:
            parent[ra] = rb

    # Compress + bucket faces by root.
    roots = np.array([find(i) for i in range(tri_count)], dtype=np.int64)
    out: list[dict[str, Any]] = []
    seen_roots: dict[int, int] = {}
    for r in np.unique(roots):
        idx = np.where(roots == r)[0]
        if idx.size < min_face_count:
            continue
        seen_roots[int(r)] = len(out)
        out.append(_region_dict(
            region_id=f"sidecar/region_growing/{len(out)}",
            face_indices=idx,
            face_normals=normals,
            mesh=mesh,
            classification="smooth_patch",
        ))
    return out
