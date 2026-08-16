"""Test-only shims so `import inference` / `import worker` work without the heavy runtime.

`inference.py`, `app.py` and `worker.py` import torch, ultralytics, open_clip, fastapi, pydantic,
pika, uvicorn and prometheus_client at module scope, but no model is *loaded* at import time —
that happens inside `load_models()`. Downloading a couple of GB of torch to run pure-logic tests
is not a good trade, so anything genuinely missing is replaced with a permissive stub here.

Only missing modules are stubbed: where the real package is installed it is imported and used
untouched, so this can never mask a broken import in an environment that has the real thing.

Pillow is deliberately **not** stubbed. The truncated-JPEG tests decode real bytes, and a stub
would turn them into tests of the stub. They skip instead when Pillow is absent.
"""

import sys
import types

_OPTIONAL_MODULES = (
    "torch",
    "ultralytics",
    "open_clip",
    "fastapi",
    "pydantic",
    "pika",
    "uvicorn",
    "prometheus_client",
)


class _Stub:
    """Accepts any construction, call, attribute or union — enough to survive import time.

    `__or__` matters more than it looks: `inference.py` annotates `_yolo: YOLO | None`, and a
    module-level annotation is evaluated on import, so the stub has to support `|`.
    """

    def __init__(self, *args, **kwargs):
        pass

    def __call__(self, *args, **kwargs):
        return _Stub()

    def __getattr__(self, name):
        return _Stub()

    def __or__(self, other):
        return _Stub()

    def __ror__(self, other):
        return _Stub()

    def __enter__(self):
        # `with ai_inference_seconds.time():` in worker.py — the Histogram timer is a context
        # manager, so a stubbed prometheus_client has to be one too.
        return self

    def __exit__(self, *exc_info):
        return False

    def __mro_entries__(self, bases):
        # `class EmbedTextRequest(BaseModel)` in app.py — a stubbed base has to be subclassable.
        return (object,)


def _stub_module(name: str) -> types.ModuleType:
    module = types.ModuleType(name)
    module.__getattr__ = lambda attr: _Stub()  # type: ignore[attr-defined]
    return module


for _name in _OPTIONAL_MODULES:
    try:
        __import__(_name)
    except ImportError:
        sys.modules[_name] = _stub_module(_name)
