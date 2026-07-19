"""Local mesh-analytics sidecar used by the Unity Editor extension."""
from __future__ import annotations

import io
import math
import time
from typing import Any, Literal

import numpy as np
import trimesh
from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field

from segmenters import (
    segment_normal_cluster,
    segment_ransac_planes,
    segment_region_growing,
)
from semantic import label_planes

SERVICE_NAME = "mesh-analytics-sidecar"
SERVICE_VERSION = "1.1.0"
MAX_REQUEST_BYTES = 64 * 1024 * 1024
MAX_OBJ_CHARS = 64 * 1024 * 1024
MAX_VERTICES = 500_000
MAX_FACES = 1_000_000
SegmentationMethod = Literal[
    "semantic", "ransac_planes", "region_growing", "normal_cluster"
]


class SegmentRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    mesh_obj: str = Field(..., min_length=1, max_length=MAX_OBJ_CHARS)
    method: SegmentationMethod = "semantic"
    min_face_count: int = Field(16, ge=1, le=MAX_FACES)
    normal_buckets: int = Field(8, ge=2, le=64)
    ransac_angle_tolerance_deg: float = Field(5.0, ge=0.1, le=45.0)
    ransac_distance_tolerance: float = Field(-1.0, ge=-1.0)
    ransac_max_planes: int = Field(16, ge=1, le=64)
    region_growing_frontier_deg: float = Field(15.0, ge=1.0, le=89.0)


class SegmentRegion(BaseModel):
    region_id: str
    face_indices: list[int]
    face_count: int
    avg_normal: list[float]
    classification: str
    confidence: float = 1.0
    world_bounds: dict[str, list[float]] | None = None


class SegmentResponse(BaseModel):
    method: SegmentationMethod
    region_count: int
    regions: list[SegmentRegion]
    info: dict[str, Any]
    elapsed_ms: float


class MethodInfo(BaseModel):
    name: SegmentationMethod
    description: str
    produces_labels: bool


app = FastAPI(title=SERVICE_NAME, version=SERVICE_VERSION)


@app.middleware("http")
async def reject_oversized_requests(request: Request, call_next):
    content_length = request.headers.get("content-length")
    if content_length:
        try:
            if int(content_length) > MAX_REQUEST_BYTES:
                return JSONResponse(
                    status_code=413,
                    content={"detail": f"request body exceeds {MAX_REQUEST_BYTES} bytes"},
                )
        except ValueError:
            return JSONResponse(status_code=400, content={"detail": "invalid Content-Length"})
    return await call_next(request)


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok", "service": SERVICE_NAME, "version": SERVICE_VERSION}


@app.get("/methods", response_model=list[MethodInfo])
def methods() -> list[MethodInfo]:
    return [
        MethodInfo(
            name="semantic",
            description="Deterministic RANSAC plane segmentation with geometric labels.",
            produces_labels=True,
        ),
        MethodInfo(
            name="ransac_planes",
            description="Deterministic raw RANSAC plane segmentation.",
            produces_labels=False,
        ),
        MethodInfo(
            name="region_growing",
            description="Flood-fill across face adjacency by normal continuity.",
            produces_labels=False,
        ),
        MethodInfo(
            name="normal_cluster",
            description="Face-normal bucketing on a Fibonacci sphere lattice.",
            produces_labels=False,
        ),
    ]


@app.post("/segment", response_model=SegmentResponse)
def segment(req: SegmentRequest) -> SegmentResponse:
    started = time.perf_counter()
    mesh = _load_mesh(req.mesh_obj)
    vertex_count = int(mesh.vertices.shape[0])
    face_count = int(mesh.faces.shape[0])

    if vertex_count == 0 or face_count == 0:
        raise HTTPException(status_code=400, detail="mesh must contain vertices and faces")
    if vertex_count > MAX_VERTICES:
        raise HTTPException(
            status_code=413,
            detail=f"mesh has {vertex_count} vertices; limit is {MAX_VERTICES}",
        )
    if face_count > MAX_FACES:
        raise HTTPException(
            status_code=413,
            detail=f"mesh has {face_count} faces; limit is {MAX_FACES}",
        )
    if not np.isfinite(mesh.vertices).all():
        raise HTTPException(status_code=400, detail="mesh vertices contain NaN or infinity")

    info: dict[str, Any] = {
        "totalFaces": face_count,
        "totalVertices": vertex_count,
        "limits": {"maxFaces": MAX_FACES, "maxVertices": MAX_VERTICES},
    }

    if req.method == "normal_cluster":
        regions = segment_normal_cluster(mesh, req.normal_buckets, req.min_face_count)
    elif req.method == "ransac_planes":
        regions = _ransac(mesh, req)
    elif req.method == "region_growing":
        regions = segment_region_growing(
            mesh,
            frontier_deg=req.region_growing_frontier_deg,
            min_face_count=req.min_face_count,
        )
    else:
        regions = label_planes(mesh, _ransac(mesh, req))
        info["semantic_label_count"] = sum(
            1 for region in regions if region["classification"] != "other"
        )

    sanitized = [_sanitize_region(region, face_count) for region in regions]
    sanitized.sort(key=lambda region: (-region["face_count"], region["region_id"]))
    payload = [SegmentRegion(**region) for region in sanitized]
    elapsed_ms = (time.perf_counter() - started) * 1000.0
    if not math.isfinite(elapsed_ms):
        elapsed_ms = 0.0

    return SegmentResponse(
        method=req.method,
        region_count=len(payload),
        regions=payload,
        info=info,
        elapsed_ms=elapsed_ms,
    )


def _load_mesh(mesh_obj: str) -> trimesh.Trimesh:
    try:
        loaded = trimesh.load(io.StringIO(mesh_obj), file_type="obj", process=False)
    except Exception as exc:
        raise HTTPException(status_code=400, detail=f"failed to parse OBJ: {exc}") from exc

    if isinstance(loaded, trimesh.Scene):
        if not loaded.geometry:
            raise HTTPException(status_code=400, detail="empty scene")
        ordered = tuple(loaded.geometry[key] for key in sorted(loaded.geometry))
        mesh = trimesh.util.concatenate(ordered)
    elif isinstance(loaded, trimesh.Trimesh):
        mesh = loaded
    else:
        raise HTTPException(
            status_code=400,
            detail=f"unsupported OBJ payload: {type(loaded).__name__}",
        )
    return mesh


def _ransac(mesh: trimesh.Trimesh, req: SegmentRequest) -> list[dict[str, Any]]:
    return segment_ransac_planes(
        mesh,
        angle_tol_deg=req.ransac_angle_tolerance_deg,
        distance_tol=req.ransac_distance_tolerance,
        max_planes=req.ransac_max_planes,
        min_face_count=req.min_face_count,
        random_seed=12345,
    )


def _sanitize_region(region: dict[str, Any], total_faces: int) -> dict[str, Any]:
    clean = dict(region)
    clean.pop("_plane_area", None)
    clean.pop("_plane_point", None)

    indices = sorted({int(index) for index in clean.get("face_indices", [])})
    if any(index < 0 or index >= total_faces for index in indices):
        raise HTTPException(status_code=500, detail="segmenter returned an invalid face index")
    clean["face_indices"] = indices
    clean["face_count"] = len(indices)

    normal = np.asarray(clean.get("avg_normal", [0.0, 1.0, 0.0]), dtype=np.float64)
    if normal.shape != (3,) or not np.isfinite(normal).all():
        raise HTTPException(status_code=500, detail="segmenter returned an invalid normal")
    clean["avg_normal"] = normal.tolist()

    confidence = float(clean.get("confidence", 1.0))
    clean["confidence"] = confidence if math.isfinite(confidence) else 0.0
    bounds = clean.get("world_bounds")
    if bounds is not None:
        for key in ("min", "max"):
            values = np.asarray(bounds.get(key), dtype=np.float64)
            if values.shape != (3,) or not np.isfinite(values).all():
                raise HTTPException(status_code=500, detail="segmenter returned invalid bounds")
            bounds[key] = values.tolist()
    return clean
