import tomllib
from dataclasses import dataclass, fields
from pathlib import Path


@dataclass
class Config:
    # capture
    poll_hz: float = 4.0
    backend: str = "auto"
    only_when_focused: bool = False
    roi: tuple = (0.25, 0.08, 0.75, 0.50)
    # detect
    gate_threshold: float = 0.005
    gate_hue: tuple = (22, 34)
    ocr_language: str = "en"
    anchor: str = "YOU FOUND"
    banner_gap_s: float = 1.0
    snap_score: int = 80
    # icons
    icon_folder: str = "assets"
    learn_icons: bool = True
    generic_names: tuple = ("Sprite Gizmo",)
    # session
    process_names: tuple = ("FortniteClient-Win64-Shipping.exe",)
    end_grace_s: float = 10.0
    # output
    folder: str = "logs"
    # hotkeys ([hotkey] section: reset)
    hotkey_reset: str = "ctrl+shift+r"  # "" turns it off
    # overlay ([overlay] section: enabled, x, y, ...)
    overlay_enabled: bool = False
    overlay_x: int = 16  # offset from the game window's top-left corner, in pixels
    overlay_y: int = 16
    overlay_scale: float = 1.0
    overlay_opacity: float = 0.85
    overlay_items: int = 5  # recent finds listed under the timers
    overlay_show_names: bool = False


def load_config(path: Path) -> Config:
    cfg = Config()
    if not path.exists():
        return cfg
    data = tomllib.loads(path.read_text(encoding="utf-8"))
    known = {f.name for f in fields(Config)}
    for section_name, section in data.items():
        if not isinstance(section, dict):
            continue
        for key, value in section.items():
            name = key if key in known else f"{section_name}_{key}"  # [overlay] x -> overlay_x
            if name not in known:
                print(f"config: ignoring unknown setting '{key}' in [{section_name}]")
                continue
            setattr(cfg, name, tuple(value) if isinstance(value, list) else value)
    return cfg
