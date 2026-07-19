"""
Semantic labeller — takes raw RANSAC plane regions produced by
segmenters.segment_ransac_planes and assigns human-readable class names
based on relational geometric heuristics.

Assumes Y-up world convention (Unity default). For Z-up scenes the
ground/ceiling distinction won't work; you'd need to swap axes before
posting the mesh.

Label vocabulary (extend in `_label_plane` as needed):
    ground            — largest horizontal_up plane at the lowest Y
    road              — same as ground but with long-rectangular aspect
    sidewalk_top      — horizontal_up raised slightly above ground
    platform          — horizontal_up at intermediate height, smaller
    roof_top          — horizontal_up at the highest level
    ceiling           — horizontal_down at high Y
    wall_<bearing>    — large vertical plane (bearing = degrees CCW from +X)
    door_candidate    — narrow tall vertical plane with bottom near ground
    window_candidate  — narrow vertical plane elevated above ground
    step / curb       — small horizontal_up between ground and platform
    other             — fallback
"""
from __future__ import annotations

import math
from typing import Any

import numpy as np
import trimesh


# Tunables. Public so callers can override per request if needed (today
# they're just module-level constants — keep it simple for V3).
HORIZONTAL_THRESHOLD_DEG = 30.0           # angle from world-up to count as horizontal_up/down
GROUND_AREA_RELATIVE_THRESHOLD = 0.05     # fraction of scene footprint a "ground" must cover
WALL_AREA_RELATIVE_THRESHOLD = 0.02
DOOR_HEIGHT_RANGE = (1.7, 2.6)            # metres
WINDOW_BOTTOM_MIN = 0.6                   # metres above ground
DOOR_ASPECT_HEIGHT_OVER_WIDTH = 1.4
CURB_HEIGHT_RANGE = (0.05, 0.25)


def label_planes(
    mesh: trimesh.Trimesh,
    planes: list[dict[str, Any]],
) -> list[dict[str, Any]]:
    """Label each plane region with a semantic class. Returns the same
    regions with `classification` (and `confidence`) replaced."""
    if not planes:
        return planes

    # Global scene bounds: gives us "ground level Y" and total footprint.
    mn, mx = mesh.bounds
    scene_y_min = float(mn[1])
    scene_y_max = float(mx[1])
    scene_height = max(scene_y_max - scene_y_min, 1e-3)
    scene_footprint = float((mx[0] - mn[0]) * (mx[2] - mn[2])) or 1.0

    # Precompute per-plane derived properties.
    derived: list[dict[str, Any]] = []
    for p in planes:
        n = np.asarray(p["avg_normal"], dtype=np.float64)
        n = n / (np.linalg.norm(n) or 1.0)
        bb_min = np.asarray(p["world_bounds"]["min"], dtype=np.float64)
        bb_max = np.asarray(p["world_bounds"]["max"], dtype=np.float64)
        size = bb_max - bb_min
        area = float(p.get("_plane_area", 0.0)) or _approx_area_from_bounds(size, n)
        orient, bearing = _orientation(n)
        derived.append({
            "normal": n,
            "bb_min": bb_min,
            "bb_max": bb_max,
            "size": size,
            "area": area,
            "centroid_y": float((bb_min[1] + bb_max[1]) * 0.5),
            "orientation": orient,
            "bearing": bearing,
        })

    # Identify the canonical ground plane: largest horizontal_up with
    # bottom Y closest to scene_y_min.
    horizontal_up_indices = [
        i for i, d in enumerate(derived) if d["orientation"] == "horizontal_up"
    ]
    ground_idx: int | None = None
    if horizontal_up_indices:
        def ground_score(i: int) -> tuple[float, float]:
            d = derived[i]
            y_penalty = (d["bb_min"][1] - scene_y_min)         # smaller is better
            return (y_penalty, -d["area"])
        ground_idx = min(horizontal_up_indices, key=ground_score)
        # Reject if the chosen "ground" is implausibly small.
        if derived[ground_idx]["area"] < GROUND_AREA_RELATIVE_THRESHOLD * scene_footprint:
            ground_idx = None

    ground_y = derived[ground_idx]["bb_max"][1] if ground_idx is not None else scene_y_min

    # Walls: largest verticals with bottom near ground.
    wall_indices = {
        i for i, d in enumerate(derived)
        if d["orientation"].startswith("vertical")
        and d["area"] >= WALL_AREA_RELATIVE_THRESHOLD * scene_footprint
        and d["bb_min"][1] <= ground_y + 0.5      # foot near ground
    }

    # Per-plane labelling pass.
    for i, p in enumerate(planes):
        d = derived[i]
        label, conf = _label_plane(
            d=d,
            is_ground=(i == ground_idx),
            wall_indices=wall_indices,
            i=i,
            ground_y=ground_y,
            scene_y_min=scene_y_min,
            scene_y_max=scene_y_max,
            scene_height=scene_height,
            scene_footprint=scene_footprint,
        )
        p["classification"] = label
        p["confidence"] = conf
        # Strip internal-only helpers used during labelling.
        p.pop("_plane_area", None)
        p.pop("_plane_point", None)

    return planes


def _label_plane(
    *,
    d: dict[str, Any],
    is_ground: bool,
    wall_indices: set[int],
    i: int,
    ground_y: float,
    scene_y_min: float,
    scene_y_max: float,
    scene_height: float,
    scene_footprint: float,
) -> tuple[str, float]:
    """Single-plane classification given the per-plane derived properties
    + a few scene-level numbers. Returns (label, confidence ∈ [0,1])."""
    orient = d["orientation"]
    area = d["area"]
    size = d["size"]
    bb_min = d["bb_min"]
    bb_max = d["bb_max"]

    rel_area = area / max(scene_footprint, 1e-3)
    height_above_ground = max(bb_min[1] - ground_y, 0.0)

    # --- horizontal_up family ---
    if orient == "horizontal_up":
        if is_ground:
            # Ground vs road: a road is much longer than wide.
            longer = max(size[0], size[2])
            shorter = min(size[0], size[2])
            if shorter > 1e-3 and longer / shorter > 4.0:
                return ("road", 0.75)
            return ("ground", 0.9)

        # Curb / step: very thin horizontal slab just above ground.
        if (CURB_HEIGHT_RANGE[0] <= height_above_ground <= CURB_HEIGHT_RANGE[1]
                and rel_area < 0.05):
            return ("curb", 0.7)

        # Sidewalk_top: raised <0.5m above ground, area moderate.
        if height_above_ground <= 0.5 and rel_area >= 0.01:
            return ("sidewalk_top", 0.75)

        # Roof_top: near the top of the scene.
        if bb_max[1] >= scene_y_max - scene_height * 0.1:
            return ("roof_top", 0.7)

        # Otherwise a generic raised platform.
        return ("platform", 0.6)

    # --- horizontal_down ---
    if orient == "horizontal_down":
        if bb_max[1] >= scene_y_max - scene_height * 0.25:
            return ("ceiling", 0.85)
        return ("overhang", 0.55)

    # --- vertical family ---
    # `orientation` is "vertical:NNN" with bearing.
    bearing = d["bearing"]
    bearing_label = f"{int(round(bearing)):03d}"

    height = size[1]
    width = max(size[0], size[2])

    is_wall = i in wall_indices

    if is_wall:
        return (f"wall_{bearing_label}", 0.85)

    # Door candidate: narrow tall vertical, bottom near ground.
    if (DOOR_HEIGHT_RANGE[0] <= height <= DOOR_HEIGHT_RANGE[1]
            and width <= 1.5
            and width > 0.05
            and height / max(width, 1e-3) >= DOOR_ASPECT_HEIGHT_OVER_WIDTH
            and bb_min[1] <= ground_y + 0.3):
        return ("door_candidate", 0.7)

    # Window candidate: vertical, elevated, modest aspect.
    if (height > 0.3 and height < 2.0
            and width > 0.2 and width < 3.0
            and bb_min[1] >= ground_y + WINDOW_BOTTOM_MIN):
        return ("window_candidate", 0.6)

    return (f"vertical_other_{bearing_label}", 0.4)


# ----- helpers -----


def _orientation(world_normal: np.ndarray) -> tuple[str, float]:
    """Return (orientation_kind, bearing_degrees). Bearing is meaningful
    only for verticals; 0 for horizontals."""
    cos_thresh = math.cos(math.radians(HORIZONTAL_THRESHOLD_DEG))
    dot_up = float(world_normal[1])      # n · (0,1,0)
    if dot_up > cos_thresh:
        return ("horizontal_up", 0.0)
    if dot_up < -cos_thresh:
        return ("horizontal_down", 0.0)
    bearing = math.degrees(math.atan2(world_normal[2], world_normal[0]))
    if bearing < 0:
        bearing += 360.0
    return (f"vertical:{int(round(bearing))}", bearing)


def _approx_area_from_bounds(size: np.ndarray, normal: np.ndarray) -> float:
    """Fallback area estimate from AABB extent perpendicular to the plane
    normal. Underestimates folded/curved geometry but works for true
    planar regions (which is what RANSAC gives us)."""
    n = np.abs(normal)
    # Take the two AABB sides most perpendicular to the normal.
    axes = sorted(range(3), key=lambda a: n[a])[:2]
    return float(size[axes[0]] * size[axes[1]])
