"""System-wide hotkeys (RegisterHotKey), so you can act without leaving the game."""
import ctypes
import threading
from ctypes import wintypes

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
user32.RegisterHotKey.argtypes = [wintypes.HWND, ctypes.c_int, wintypes.UINT, wintypes.UINT]
user32.UnregisterHotKey.argtypes = [wintypes.HWND, ctypes.c_int]
user32.GetMessageW.argtypes = [ctypes.POINTER(wintypes.MSG), wintypes.HWND, wintypes.UINT, wintypes.UINT]
user32.PostThreadMessageW.argtypes = [wintypes.DWORD, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]

MODIFIERS = {"alt": 0x1, "ctrl": 0x2, "control": 0x2, "shift": 0x4, "win": 0x8}
MOD_NOREPEAT = 0x4000
WM_HOTKEY, WM_QUIT = 0x0312, 0x0012


def parse_hotkey(text: str) -> tuple[int, int]:
    """'ctrl+shift+r' -> (modifiers, virtual-key code). Keys: A-Z, 0-9, F1-F24."""
    parts = [p.strip().lower() for p in text.split("+") if p.strip()]
    if not parts:
        raise ValueError("empty hotkey")
    mods = 0
    for p in parts[:-1]:
        if p not in MODIFIERS:
            raise ValueError(f"unknown modifier '{p}' (use ctrl, alt, shift, win)")
        mods |= MODIFIERS[p]
    key = parts[-1]
    if len(key) == 1 and key.isalnum():
        vk = ord(key.upper())
    elif key[0] == "f" and key[1:].isdigit() and 1 <= int(key[1:]) <= 24:
        vk = 0x70 + int(key[1:]) - 1
    else:
        raise ValueError(f"unsupported key '{key}' (use A-Z, 0-9 or F1-F24)")
    return mods, vk


class Hotkeys:
    """Registers hotkeys on a background thread; pressed names are collected for the main loop."""

    def __init__(self, bindings: dict[str, str]):
        self.bindings = {name: text for name, text in bindings.items() if text}
        self.registered: dict[str, str] = {}
        self.errors: list[str] = []
        self._pressed: list[str] = []
        self._lock = threading.Lock()
        self._ready = threading.Event()
        self._thread_id = 0
        self._thread = threading.Thread(target=self._run, name="hotkeys", daemon=True)

    def start(self):
        if self.bindings:
            self._thread.start()
            self._ready.wait(2)

    def pressed(self) -> list[str]:
        with self._lock:
            out, self._pressed = self._pressed, []
        return out

    def stop(self):
        if self._thread.is_alive():
            user32.PostThreadMessageW(self._thread_id, WM_QUIT, 0, 0)
            self._thread.join(timeout=2)

    def _run(self):
        self._thread_id = kernel32.GetCurrentThreadId()
        ids = {}
        for i, (name, text) in enumerate(self.bindings.items(), start=1):
            try:
                mods, vk = parse_hotkey(text)
            except ValueError as e:
                self.errors.append(f"{name} hotkey '{text}': {e}")
                continue
            if user32.RegisterHotKey(None, i, mods | MOD_NOREPEAT, vk):
                ids[i] = name
                self.registered[name] = text
            else:
                self.errors.append(f"{name} hotkey '{text}' is already used by another program")
        self._ready.set()
        msg = wintypes.MSG()
        try:
            while user32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
                if msg.message == WM_HOTKEY and msg.wParam in ids:
                    with self._lock:
                        self._pressed.append(ids[msg.wParam])
        finally:
            for i in ids:
                user32.UnregisterHotKey(None, i)
