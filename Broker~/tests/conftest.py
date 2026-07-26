from __future__ import annotations

import sys
from pathlib import Path

BROKER_ROOT = Path(__file__).resolve().parents[1]
if str(BROKER_ROOT) not in sys.path:
    sys.path.insert(0, str(BROKER_ROOT))
