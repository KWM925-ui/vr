"""Portable locations for the unchanged native CAD extraction.

Historical JSON retains its original F: paths and hashes. Resolve only known
workspace roots to the current checkout; never follow arbitrary CAD references
to another disk, network share or machine.
"""

from pathlib import Path, PureWindowsPath


ROOT = Path(__file__).resolve().parents[1]
CAD = ROOT / "hardware/paddle/cad"
ORIGINAL_ROOT = PureWindowsPath("F:/vr视觉")


def native_source(value, *, root=ROOT):
    root = Path(root).resolve()
    saved = PureWindowsPath(value)
    if not saved.is_relative_to(ORIGINAL_ROOT):
        raise ValueError("native reference is outside the original project workspace")
    relative = saved.relative_to(ORIGINAL_ROOT)
    if not relative.parts or relative.parts[0] != "10月11日修改" or ".." in relative.parts:
        raise ValueError("expected a native part inside the supplied CAD directory")
    resolved = root.joinpath(*relative.parts).resolve()
    if not resolved.is_relative_to(root / "10月11日修改"):
        raise ValueError("native reference escapes the current CAD directory")
    return resolved
