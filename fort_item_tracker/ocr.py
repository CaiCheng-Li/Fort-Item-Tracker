"""Built-in Windows OCR (Windows.Media.Ocr) through the winrt projection."""
import asyncio
from dataclasses import dataclass

import cv2
from winrt.windows.globalization import Language
from winrt.windows.graphics.imaging import BitmapPixelFormat, SoftwareBitmap
from winrt.windows.media.ocr import OcrEngine
from winrt.windows.storage.streams import DataWriter


@dataclass
class Line:
    text: str
    x0: float
    y0: float
    x1: float
    y1: float

    @property
    def cx(self) -> float:
        return (self.x0 + self.x1) / 2


async def _wait(op):
    return await op


class WindowsOcr:
    def __init__(self, language: str = "en"):
        lang = Language(language)
        if not OcrEngine.is_language_supported(lang):
            raise RuntimeError(
                f'Windows OCR has no "{language}" language pack. In an admin PowerShell run:\n'
                f'  Add-WindowsCapability -Online -Name "Language.OCR~~~en-US~0.0.1.0"'
            )
        self._engine = OcrEngine.try_create_from_language(lang)
        self._loop = asyncio.new_event_loop()

    def read(self, bgr) -> list[Line]:
        rgba = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGBA)
        writer = DataWriter()
        writer.write_bytes(rgba.tobytes())
        bitmap = SoftwareBitmap.create_copy_from_buffer(
            writer.detach_buffer(), BitmapPixelFormat.RGBA8, rgba.shape[1], rgba.shape[0]
        )
        result = self._loop.run_until_complete(_wait(self._engine.recognize_async(bitmap)))
        lines = []
        for ln in result.lines:
            boxes = [w.bounding_rect for w in ln.words]
            if not boxes:
                continue
            lines.append(
                Line(
                    ln.text,
                    min(b.x for b in boxes),
                    min(b.y for b in boxes),
                    max(b.x + b.width for b in boxes),
                    max(b.y + b.height for b in boxes),
                )
            )
        return lines

    def close(self):
        self._loop.close()
