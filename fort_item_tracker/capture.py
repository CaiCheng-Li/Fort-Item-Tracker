"""Grab a small screen region. dxcam (DXGI desktop duplication) is ~2 ms; mss (GDI) is the fallback."""
import numpy as np

from .winutil import Rect


class DxcamCapture:
    name = "dxcam"

    def __init__(self):
        import dxcam

        self._dxcam = dxcam
        factory = next(v for v in vars(dxcam).values() if isinstance(v, dxcam.DXFactory))
        self._outputs = {
            out.devicename: (di, oi)
            for di, outs in enumerate(factory.outputs)
            for oi, out in enumerate(outs)
        }
        self._cam = None
        self._key = None

    def grab(self, device: str, monitor: Rect, region: Rect):
        key = self._outputs.get(device)
        if key is None:
            raise RuntimeError(f"dxcam has no output named {device}")
        if key != self._key:
            self.close()
            self._cam = self._dxcam.create(device_idx=key[0], output_idx=key[1], output_color="BGR")
            self._key = key
        ml, mt = monitor[0], monitor[1]
        # None means the screen has not changed since the last grab.
        return self._cam.grab(region=(region[0] - ml, region[1] - mt, region[2] - ml, region[3] - mt))

    def close(self):
        if self._cam is not None:
            try:
                self._cam.release()
            except Exception:
                pass
        self._cam = None
        self._key = None


class MssCapture:
    name = "mss"

    def __init__(self):
        import mss

        self._sct = (getattr(mss, "MSS", None) or mss.mss)()

    def grab(self, device: str, monitor: Rect, region: Rect):
        shot = self._sct.grab(
            {"left": region[0], "top": region[1], "width": region[2] - region[0], "height": region[3] - region[1]}
        )
        return np.ascontiguousarray(np.asarray(shot)[:, :, :3])

    def close(self):
        self._sct.close()


def make_capture(backend: str):
    if backend in ("auto", "dxcam"):
        try:
            return DxcamCapture()
        except Exception as e:
            if backend == "dxcam":
                raise
            print(f"dxcam unavailable ({e}); using mss")
    return MssCapture()
