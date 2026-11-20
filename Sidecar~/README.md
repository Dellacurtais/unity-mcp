# Mesh-analytics sidecar

FastAPI service that runs heavier 3D segmentation than what fits in the
C# Editor plugin. Talks JSON over local HTTP; Unity speaks to it via
`mesh_segment_via_sidecar` and `mesh_sidecar_health` MCP tools.

Unity ignores this folder because of the `~` suffix — Python code lives
adjacent to the C# package without being part of the asmdef compilation.

## Quick start

```bash
cd Sidecar~   # from the package root
python -m venv .venv

# Windows PowerShell:
. .venv\Scripts\Activate.ps1
# macOS / Linux:
source .venv/bin/activate

pip install -r requirements.txt
uvicorn main:app --host 127.0.0.1 --port 8766
```

Smoke-test:

```bash
curl http://127.0.0.1:8766/health
curl http://127.0.0.1:8766/methods
```

In Unity, call `mesh_sidecar_health` via MCP — if it returns the service
version and a list of methods, you're set.

## Methods

| `method`         | What it does                                                                                                                                                                                                                              | Returns labels?            |
| ---------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------- |
| `semantic` (default) | RANSAC plane segmentation + relational geometric labelling. Each region gets a class like `ground`, `road`, `sidewalk_top`, `wall_<bearing>`, `door_candidate`, `window_candidate`, `ceiling`, `step`, `curb`, `roof_top`, `platform`. | **Yes** — human-readable.   |
| `ransac_planes`  | Same RANSAC but without the labelling pass — useful when you want raw planes and your own classifier downstream.                                                                                                                          | No (`classification=plane`) |
| `region_growing` | Flood-fill across face adjacency wherever the dihedral angle is below the frontier threshold. Captures smooth patches separated by sharp creases — terrain, sculpted props, rounded handrails.                                            | No (`smooth_patch`)         |
| `normal_cluster` | Buckets face normals onto a Fibonacci sphere lattice. Cheap baseline; not semantic but consistent.                                                                                                                                        | No (`cluster_<k>`)          |

The `semantic` method assumes **Y-up world convention** (Unity default).
For Z-up scenes, swap axes before posting (or override the `_orientation`
helper in `semantic.py`).

## API surface

```
GET  /health
        → { status, service, version }
GET  /methods
        → [ { name, description, produces_labels }, ... ]
POST /segment
        request body:
          {
            "mesh_obj":                "<Wavefront OBJ text>",
            "method":                  "semantic" | "ransac_planes" | "region_growing" | "normal_cluster",
            "min_face_count":          16,
            "normal_buckets":          8,
            "ransac_angle_tolerance_deg": 5.0,
            "ransac_distance_tolerance":  -1.0,   // -1 = auto (1% of bounds diagonal)
            "ransac_max_planes":       16,
            "region_growing_frontier_deg": 15.0
          }
        response:
          {
            "method": "semantic",
            "region_count": 7,
            "regions": [
              {
                "region_id":      "sidecar/ransac_plane/0",
                "face_indices":   [0, 1, 2, ...],
                "face_count":     1242,
                "avg_normal":     [0.0, 1.0, 0.0],
                "classification": "ground",
                "confidence":     0.9,
                "world_bounds":   { "min": [...], "max": [...] }
              },
              ...
            ],
            "info":       { "totalFaces": ..., "semantic_label_count": ... },
            "elapsed_ms": 312.7
          }
```

## Tuning the semantic labeller

Per-rule thresholds live as module-level constants at the top of
`semantic.py`. Common knobs:

| Constant                         | Default | Effect                                                                                |
| -------------------------------- | ------- | ------------------------------------------------------------------------------------- |
| `HORIZONTAL_THRESHOLD_DEG`       | 30°     | Tilt allowance for a plane to count as horizontal_up / horizontal_down.               |
| `GROUND_AREA_RELATIVE_THRESHOLD` | 0.05    | Fraction of scene footprint a plane must cover to be eligible as `ground`/`road`.     |
| `WALL_AREA_RELATIVE_THRESHOLD`   | 0.02    | Same idea for `wall_<bearing>`.                                                       |
| `DOOR_HEIGHT_RANGE`              | (1.7, 2.6) m | Door height window. Outside this → `vertical_other_*` or `window_candidate`.    |
| `WINDOW_BOTTOM_MIN`              | 0.6 m   | A small vertical with `bb_min.y < ground + WINDOW_BOTTOM_MIN` is *not* a window.       |
| `CURB_HEIGHT_RANGE`              | (0.05, 0.25) m | Thin horizontal slabs in this Y-range above ground become `curb`.              |

Adjust to match the scale of your assets (a roomscale arch viz vs a city
block needs different thresholds).

## Performance notes

- The pure-numpy implementation handles **~50k faces in under 1s** on a
  modern CPU with `method=semantic`. For ~500k faces, expect 5–10s; cap
  via `ransac_max_planes` if you only need the top few planes.
- The C# side blocks the Editor main thread while the request is
  in-flight (it's a synchronous tool call). The default tool timeout is
  60s; raise via the `timeoutSeconds` arg if needed.
- For very large meshes, consider running with `method=ransac_planes`
  (skips the labelling pass) and letting the in-process detectors fill
  in classification.

## Plugging in a real ML model

The labelling in `semantic.py` is heuristic — pure geometry, no
training. If you need open-vocabulary semantic segmentation
(OpenScene / OpenMask3D / SAM-3D), the natural slot is to add a new
method branch in `main.py`:

```python
elif req.method == "openscene":
    regions = segment_openscene(mesh, ...)
```

Module-level cache the model so first-request latency is paid once.
Return the same `SegmentRegion` shape — the C# side is method-agnostic
and renders whatever class names you emit.
