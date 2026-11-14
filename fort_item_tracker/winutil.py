"""Small Win32 helpers: DPI awareness, finding the game window and its monitor."""
import ctypes
from ctypes import wintypes

user32 = ctypes.WinDLL("user32", use_last_error=True)

HMONITOR = ctypes.c_void_p
WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
MONITORENUMPROC = ctypes.WINFUNCTYPE(
    wintypes.BOOL, HMONITOR, wintypes.HDC, ctypes.POINTER(wintypes.RECT), wintypes.LPARAM
)


class MONITORINFOEXW(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.DWORD),
        ("rcMonitor", wintypes.RECT),
        ("rcWork", wintypes.RECT),
        ("dwFlags", wintypes.DWORD),
        ("szDevice", wintypes.WCHAR * 32),
    ]


user32.EnumWindows.argtypes = [WNDENUMPROC, wintypes.LPARAM]
user32.IsWindow.argtypes = [wintypes.HWND]
user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.IsIconic.argtypes = [wintypes.HWND]
user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
user32.GetClientRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
user32.ClientToScreen.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.POINT)]
user32.GetForegroundWindow.restype = wintypes.HWND
user32.MonitorFromWindow.argtypes = [wintypes.HWND, wintypes.DWORD]
user32.MonitorFromWindow.restype = HMONITOR
user32.GetMonitorInfoW.argtypes = [HMONITOR, ctypes.POINTER(MONITORINFOEXW)]
user32.EnumDisplayMonitors.argtypes = [wintypes.HDC, ctypes.c_void_p, MONITORENUMPROC, wintypes.LPARAM]

Rect = tuple[int, int, int, int]  # left, top, right, bottom in physical screen pixels


def enable_dpi_awareness() -> None:
    """Use physical pixels everywhere, so window and capture coordinates agree."""
    try:
        fn = user32.SetProcessDpiAwarenessContext
        fn.argtypes = [ctypes.c_void_p]
        if fn(ctypes.c_void_p(-4)):  # per-monitor aware v2
            return
    except AttributeError:
        pass
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        pass


def find_main_window(pids: set[int]):
    """Largest visible top-level window owned by one of `pids`."""
    best, best_area = None, 0

    def callback(hwnd, _):
        nonlocal best, best_area
        if not user32.IsWindowVisible(hwnd):
            return True
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value in pids:
            r = wintypes.RECT()
            user32.GetClientRect(hwnd, ctypes.byref(r))
            area = r.right * r.bottom
            if area > best_area:
                best, best_area = hwnd, area
        return True

    user32.EnumWindows(WNDENUMPROC(callback), 0)
    return best


def is_window(hwnd) -> bool:
    return bool(hwnd) and bool(user32.IsWindow(hwnd))


def is_minimized(hwnd) -> bool:
    return bool(user32.IsIconic(hwnd))


def is_foreground(hwnd) -> bool:
    return user32.GetForegroundWindow() == hwnd


def client_rect(hwnd) -> Rect | None:
    r = wintypes.RECT()
    if not user32.GetClientRect(hwnd, ctypes.byref(r)) or r.right <= 0 or r.bottom <= 0:
        return None
    pt = wintypes.POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(pt))
    return (pt.x, pt.y, pt.x + r.right, pt.y + r.bottom)


def _monitor_info(hmon) -> tuple[str, Rect]:
    mi = MONITORINFOEXW()
    mi.cbSize = ctypes.sizeof(mi)
    user32.GetMonitorInfoW(hmon, ctypes.byref(mi))
    r = mi.rcMonitor
    return mi.szDevice, (r.left, r.top, r.right, r.bottom)


def monitor_for_window(hwnd) -> tuple[str, Rect]:
    return _monitor_info(user32.MonitorFromWindow(hwnd, 2))  # MONITOR_DEFAULTTONEAREST


def list_monitors() -> list[tuple[str, Rect, bool]]:
    """(device name, rect, is_primary) for every display."""
    out = []

    def callback(hmon, _hdc, _rect, _data):
        mi = MONITORINFOEXW()
        mi.cbSize = ctypes.sizeof(mi)
        user32.GetMonitorInfoW(hmon, ctypes.byref(mi))
        r = mi.rcMonitor
        out.append((mi.szDevice, (r.left, r.top, r.right, r.bottom), bool(mi.dwFlags & 1)))
        return True

    user32.EnumDisplayMonitors(None, None, MONITORENUMPROC(callback), 0)
    return out
