"""Always-on-top, click-through overlay: session timer, time since the last find, recent finds.

A plain Win32 layered window on its own thread, drawn with Pillow and shown with per-pixel
transparency. It is created hidden and only ever shown with SW_SHOWNOACTIVATE, so it never
takes focus from the game (tkinter activates its first window, which is why it isn't used).
"""
import ctypes
import os
import threading
import time
from ctypes import wintypes
from dataclasses import dataclass

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

from .session import fmt_duration

PANEL = (18, 22, 28)
FG = (255, 255, 255, 255)
DIM = (205, 213, 224, 255)
ACCENT = (255, 211, 77, 255)

# ---- Win32 -------------------------------------------------------------------------------

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

LRESULT = ctypes.c_ssize_t
WNDPROC = ctypes.WINFUNCTYPE(LRESULT, wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM)


class WNDCLASSEXW(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.UINT), ("style", wintypes.UINT), ("lpfnWndProc", WNDPROC),
        ("cbClsExtra", ctypes.c_int), ("cbWndExtra", ctypes.c_int), ("hInstance", wintypes.HINSTANCE),
        ("hIcon", wintypes.HICON), ("hCursor", wintypes.HANDLE), ("hbrBackground", wintypes.HBRUSH),
        ("lpszMenuName", wintypes.LPCWSTR), ("lpszClassName", wintypes.LPCWSTR), ("hIconSm", wintypes.HICON),
    ]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG), ("biHeight", wintypes.LONG),
        ("biPlanes", wintypes.WORD), ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
        ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", wintypes.LONG), ("biYPelsPerMeter", wintypes.LONG),
        ("biClrUsed", wintypes.DWORD), ("biClrImportant", wintypes.DWORD),
    ]


class BLENDFUNCTION(ctypes.Structure):
    _fields_ = [("BlendOp", ctypes.c_ubyte), ("BlendFlags", ctypes.c_ubyte),
                ("SourceConstantAlpha", ctypes.c_ubyte), ("AlphaFormat", ctypes.c_ubyte)]


user32.DefWindowProcW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.DefWindowProcW.restype = LRESULT
user32.RegisterClassExW.argtypes = [ctypes.POINTER(WNDCLASSEXW)]
user32.RegisterClassExW.restype = wintypes.ATOM
user32.UnregisterClassW.argtypes = [wintypes.LPCWSTR, wintypes.HINSTANCE]
user32.CreateWindowExW.argtypes = [
    wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.DWORD, ctypes.c_int, ctypes.c_int,
    ctypes.c_int, ctypes.c_int, wintypes.HWND, wintypes.HMENU, wintypes.HINSTANCE, wintypes.LPVOID,
]
user32.CreateWindowExW.restype = wintypes.HWND
user32.DestroyWindow.argtypes = [wintypes.HWND]
user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
user32.SetWindowPos.argtypes = [
    wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wintypes.UINT
]
user32.GetDC.argtypes = [wintypes.HWND]
user32.GetDC.restype = wintypes.HDC
user32.ReleaseDC.argtypes = [wintypes.HWND, wintypes.HDC]
user32.UpdateLayeredWindow.argtypes = [
    wintypes.HWND, wintypes.HDC, ctypes.POINTER(wintypes.POINT), ctypes.POINTER(wintypes.SIZE), wintypes.HDC,
    ctypes.POINTER(wintypes.POINT), wintypes.COLORREF, ctypes.POINTER(BLENDFUNCTION), wintypes.DWORD,
]
user32.PeekMessageW.argtypes = [ctypes.POINTER(wintypes.MSG), wintypes.HWND, wintypes.UINT, wintypes.UINT, wintypes.UINT]
user32.TranslateMessage.argtypes = [ctypes.POINTER(wintypes.MSG)]
user32.DispatchMessageW.argtypes = [ctypes.POINTER(wintypes.MSG)]
user32.MsgWaitForMultipleObjects.argtypes = [
    wintypes.DWORD, ctypes.c_void_p, wintypes.BOOL, wintypes.DWORD, wintypes.DWORD
]
gdi32.CreateCompatibleDC.argtypes = [wintypes.HDC]
gdi32.CreateCompatibleDC.restype = wintypes.HDC
gdi32.DeleteDC.argtypes = [wintypes.HDC]
gdi32.CreateDIBSection.argtypes = [
    wintypes.HDC, ctypes.POINTER(BITMAPINFOHEADER), wintypes.UINT, ctypes.POINTER(ctypes.c_void_p),
    wintypes.HANDLE, wintypes.DWORD,
]
gdi32.CreateDIBSection.restype = wintypes.HBITMAP
gdi32.SelectObject.argtypes = [wintypes.HDC, wintypes.HGDIOBJ]
gdi32.SelectObject.restype = wintypes.HGDIOBJ
gdi32.DeleteObject.argtypes = [wintypes.HGDIOBJ]
kernel32.GetModuleHandleW.argtypes = [wintypes.LPCWSTR]
kernel32.GetModuleHandleW.restype = wintypes.HMODULE

EX_STYLE = 0x8 | 0x20 | 0x80 | 0x80000 | 0x08000000  # TOPMOST TRANSPARENT TOOLWINDOW LAYERED NOACTIVATE
WS_POPUP = 0x80000000
SW_HIDE, SW_SHOWNOACTIVATE = 0, 4
HWND_TOPMOST = wintypes.HWND(-1)
SWP_KEEP = 0x0001 | 0x0002 | 0x0010  # NOSIZE NOMOVE NOACTIVATE
ULW_ALPHA = 2
PM_REMOVE = 1
QS_ALLINPUT = 0x04FF
CLASS_NAME = "FortItemTrackerOverlay"

# ---- state shared with the tracker -------------------------------------------------------


@dataclass(frozen=True)
class OverlayItem:
    name: str
    time: str  # clock time of the latest find, e.g. 8:31:44 PM
    icon: str | None  # image file, if the item has one
    count: int = 1  # how many found this session


@dataclass(frozen=True)
class OverlayState:
    visible: bool = False
    x: int = 0
    y: int = 0
    timer_at: float = 0.0  # master timer value at mono_ref
    mono_ref: float = 0.0
    last_item_t: float | None = None  # master timer value of the most recent find
    items: tuple[OverlayItem, ...] = ()  # newest first

    def timer(self, now: float) -> float:
        return self.timer_at + now - self.mono_ref


class Overlay:
    def __init__(self, scale: float = 1.0, opacity: float = 0.85, max_items: int = 5, show_names: bool = False):
        self.scale = max(0.5, float(scale))
        self.opacity = min(1.0, max(0.2, float(opacity)))
        self.max_items = max(0, int(max_items))
        self.show_names = show_names
        self.error: str | None = None
        self._state = OverlayState()
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, name="overlay", daemon=True)
        self._fonts = self._load_fonts()
        self._icons: dict[str, Image.Image | None] = {}

    def start(self):
        self._thread.start()

    def update(self, state: OverlayState):
        self._state = state  # replaced whole, so the overlay thread always sees a consistent state

    def stop(self):
        self._stop.set()
        self._thread.join(timeout=3)

    # ---- drawing ------------------------------------------------------------------------

    def _px(self, v: float) -> int:
        return max(1, int(round(v * self.scale)))

    def _load_fonts(self):
        folder = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts")

        def font(name, size):
            try:
                return ImageFont.truetype(os.path.join(folder, name), self._px(size))
            except OSError:
                return ImageFont.load_default(self._px(size))

        return {
            "timer": font("seguisb.ttf", 28),
            "since": font("segoeui.ttf", 15),
            "row": font("segoeui.ttf", 16),
            "count": font("seguisb.ttf", 17),
        }

    def _icon(self, path: str | None) -> Image.Image | None:
        if not path:
            return None
        if path not in self._icons:
            icon = None
            data = cv2.imdecode(np.fromfile(path, np.uint8), cv2.IMREAD_UNCHANGED) if os.path.exists(path) else None
            if data is not None:
                if data.ndim == 2:
                    data = cv2.cvtColor(data, cv2.COLOR_GRAY2BGRA)
                elif data.shape[2] == 3:
                    data = cv2.cvtColor(data, cv2.COLOR_BGR2BGRA)
                icon = Image.fromarray(cv2.cvtColor(data, cv2.COLOR_BGRA2RGBA))
                icon.thumbnail((self._px(30), self._px(30)), Image.LANCZOS)
            self._icons[path] = icon
        return self._icons[path]

    def render(self, st: OverlayState, now: float) -> Image.Image:
        f, px = self._fonts, self._px
        pad = px(12)
        timer = st.timer(now)
        lines = [("timer", fmt_duration(timer), FG)]
        if st.last_item_t is not None:
            lines.append(("since", f"Since last item  {fmt_duration(timer - st.last_item_t)}", ACCENT))
        rows = []
        for item in st.items[: self.max_items]:
            icon = self._icon(item.icon)
            text = item.time if icon is not None and not self.show_names else f"{item.time}   {item.name}"
            rows.append((icon, f"x{item.count}", text))

        measure = ImageDraw.Draw(Image.new("RGBA", (1, 1)))
        icon_box, row_h = px(30), px(34)
        count_w = max((measure.textlength(c, font=f["count"]) for _, c, _ in rows), default=0)
        text_x = icon_box + px(4) + int(count_w) + px(10)  # icon | x3 | time, in fixed columns
        width = max(measure.textlength(text, font=f[key]) for key, text, _ in lines)
        for _, _, text in rows:
            width = max(width, text_x + measure.textlength(text, font=f["row"]))
        line_h = {"timer": px(38), "since": px(22)}
        height = sum(line_h[key] for key, _, _ in lines) + len(rows) * row_h + (px(4) if rows else 0)
        w, h = int(width) + 2 * pad, height + 2 * px(6)

        img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        draw = ImageDraw.Draw(img)
        draw.rounded_rectangle((0, 0, w - 1, h - 1), radius=px(8), fill=(*PANEL, int(255 * self.opacity)))
        y = px(6)
        for key, text, colour in lines:
            draw.text((pad, y + line_h[key] // 2), text, font=f[key], fill=colour, anchor="lm")
            y += line_h[key]
        y += px(4) if rows else 0
        for icon, count, text in rows:  # the icon column is always reserved so everything lines up
            if icon is not None:
                img.alpha_composite(icon, (pad + (icon_box - icon.width) // 2, y + (row_h - icon.height) // 2))
            draw.text((pad + icon_box + px(4), y + row_h // 2), count, font=f["count"], fill=FG, anchor="lm")
            draw.text((pad + text_x, y + row_h // 2), text, font=f["row"], fill=DIM, anchor="lm")
            y += row_h
        return img

    # ---- window -------------------------------------------------------------------------

    def _run(self):
        hinst = kernel32.GetModuleHandleW(None)
        self._wndproc = WNDPROC(lambda h, m, w, l: user32.DefWindowProcW(h, m, w, l))  # keep a reference
        wc = WNDCLASSEXW(cbSize=ctypes.sizeof(WNDCLASSEXW), lpfnWndProc=self._wndproc, hInstance=hinst,
                         lpszClassName=CLASS_NAME)
        user32.RegisterClassExW(ctypes.byref(wc))
        hwnd = user32.CreateWindowExW(EX_STYLE, CLASS_NAME, "Fort Item Tracker", WS_POPUP, 0, 0, 1, 1,
                                      None, None, hinst, None)
        if not hwnd:
            self.error = f"could not create the overlay window (error {ctypes.get_last_error()})"
            print(f"\n{self.error}")
            return
        shown, drawn_key, next_raise = False, None, 0.0
        msg = wintypes.MSG()
        try:
            while not self._stop.is_set():
                while user32.PeekMessageW(ctypes.byref(msg), None, 0, 0, PM_REMOVE):
                    user32.TranslateMessage(ctypes.byref(msg))
                    user32.DispatchMessageW(ctypes.byref(msg))
                st, now = self._state, time.monotonic()
                if not st.visible:
                    if shown:
                        user32.ShowWindow(hwnd, SW_HIDE)
                        shown = False
                else:
                    key = (st, int(st.timer(now)))  # redraw when the state or the shown second changes
                    if key != drawn_key:
                        self._present(hwnd, self.render(st, now), st.x, st.y)
                        drawn_key = key
                    if not shown:
                        user32.ShowWindow(hwnd, SW_SHOWNOACTIVATE)
                        shown = True
                    if now >= next_raise:  # stay above the game without taking focus
                        user32.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_KEEP)
                        next_raise = now + 2.0
                user32.MsgWaitForMultipleObjects(0, None, False, 250, QS_ALLINPUT)
        except Exception as e:  # never take the tracker down with the overlay
            self.error = str(e)
            print(f"\noverlay stopped: {e}")
        finally:
            user32.DestroyWindow(hwnd)
            user32.UnregisterClassW(CLASS_NAME, hinst)

    @staticmethod
    def _present(hwnd, img: Image.Image, x: int, y: int):
        """Copy a straight-alpha RGBA image into the layered window (needs premultiplied BGRA)."""
        rgba = np.asarray(img, dtype=np.uint16)
        a = rgba[..., 3:4]
        bgra = np.concatenate([(rgba[..., 2::-1] * a // 255), a], axis=2).astype(np.uint8)
        h, w = bgra.shape[:2]
        screen = user32.GetDC(None)
        mem = gdi32.CreateCompatibleDC(screen)
        bmi = BITMAPINFOHEADER(biSize=ctypes.sizeof(BITMAPINFOHEADER), biWidth=w, biHeight=-h, biPlanes=1,
                               biBitCount=32)
        bits = ctypes.c_void_p()
        bmp = gdi32.CreateDIBSection(screen, ctypes.byref(bmi), 0, ctypes.byref(bits), None, 0)
        old = gdi32.SelectObject(mem, bmp)
        try:
            ctypes.memmove(bits, np.ascontiguousarray(bgra).ctypes.data, w * h * 4)
            blend = BLENDFUNCTION(0, 0, 255, 1)  # AC_SRC_OVER, per-pixel alpha
            user32.UpdateLayeredWindow(hwnd, screen, ctypes.byref(wintypes.POINT(x, y)),
                                       ctypes.byref(wintypes.SIZE(w, h)), mem, ctypes.byref(wintypes.POINT(0, 0)),
                                       0, ctypes.byref(blend), ULW_ALPHA)
        finally:
            gdi32.SelectObject(mem, old)
            gdi32.DeleteObject(bmp)
            gdi32.DeleteDC(mem)
            user32.ReleaseDC(None, screen)
