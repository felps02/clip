"""Small numeric helpers shared by the analysis modules."""

from __future__ import annotations

import math
from collections.abc import Iterable, Iterator

import numpy as np
from scipy.ndimage import median_filter, uniform_filter1d

Gaps = list[tuple[float, float]]


def coverage_fraction(gaps: Iterable[tuple[float, float]], start: float, end: float) -> float:
    """Share of ``[start, end)`` that a signal analysed (outside every gap), 0..1."""
    length = float(end) - float(start)
    if length <= 0:
        return 1.0
    missing = sum(max(0.0, min(float(e), end) - max(float(s), start)) for s, e in gaps)
    return float(min(1.0, max(0.0, 1.0 - missing / length)))


def covered_mask(gaps: Iterable[tuple[float, float]], n: int) -> np.ndarray:
    """Per-second mask: ``True`` when the middle of second ``t`` lies outside every gap."""
    mask = np.ones(max(0, int(n)), dtype=bool)
    for s, e in gaps:
        first = max(0, math.ceil(float(s) - 0.5))
        last = min(len(mask), math.ceil(float(e) - 0.5))
        if last > first:
            mask[first:last] = False
    return mask


def moving_average(x: np.ndarray, size: int) -> np.ndarray:
    """Centered moving average with edge replication."""
    if len(x) == 0 or size <= 1:
        return np.asarray(x, dtype=np.float64)
    size = min(size, len(x))
    return uniform_filter1d(np.asarray(x, dtype=np.float64), size=size, mode="nearest")


def rolling_median(x: np.ndarray, window: int) -> np.ndarray:
    if len(x) == 0:
        return np.asarray(x, dtype=np.float64)
    window = max(1, min(int(window), len(x)))
    if window % 2 == 0:
        window = max(1, window - 1)
    if window == 1:
        return np.asarray(x, dtype=np.float64).copy()
    return median_filter(np.asarray(x, dtype=np.float64), size=window, mode="nearest")


def robust_rolling_z(x: np.ndarray, window: int = 91, min_sigma: float = 1e-3) -> np.ndarray:
    """Robust z-score against a rolling baseline: ``(x - median) / (1.4826 * MAD)``.

    ``min_sigma`` floors the scale so constant signals do not explode; NaNs become 0.
    """
    x = np.asarray(x, dtype=np.float64)
    if len(x) == 0:
        return x
    med = rolling_median(x, window)
    mad = rolling_median(np.abs(x - med), window)
    sigma = np.maximum(1.4826 * mad, min_sigma)
    z = (x - med) / sigma
    return np.nan_to_num(z, nan=0.0, posinf=0.0, neginf=0.0)


def find_runs(
    mask: np.ndarray, min_len: int = 1, max_len: int | None = None
) -> Iterator[tuple[int, int]]:
    """Yield ``(start, end_exclusive)`` of runs of ``True`` with the given length bounds."""
    mask = np.asarray(mask, dtype=bool)
    if mask.size == 0:
        return
    padded = np.concatenate([[False], mask, [False]])
    diff = np.diff(padded.astype(np.int8))
    starts = np.flatnonzero(diff == 1)
    ends = np.flatnonzero(diff == -1)
    for s, e in zip(starts, ends, strict=True):
        length = e - s
        if length < min_len:
            continue
        if max_len is not None and length > max_len:
            continue
        yield int(s), int(e)


def per_second(
    values: np.ndarray, per_sec: int, n_sec: int, reducer: str = "max", fill: float = 0.0
) -> np.ndarray:
    """Reduce a frame-rate series (``per_sec`` frames per second) to ``n_sec`` seconds."""
    values = np.asarray(values, dtype=np.float64)
    needed = n_sec * per_sec
    if len(values) < needed:
        values = np.concatenate([values, np.full(needed - len(values), fill, dtype=np.float64)])
    grid = values[:needed].reshape(n_sec, per_sec)
    if reducer == "max":
        return grid.max(axis=1)
    if reducer == "mean":
        return grid.mean(axis=1)
    if reducer == "sum":
        return grid.sum(axis=1)
    if reducer == "min":
        return grid.min(axis=1)
    raise ValueError(reducer)


def clip01(x: np.ndarray) -> np.ndarray:
    return np.clip(np.asarray(x, dtype=np.float64), 0.0, 1.0)


def rounded(values: np.ndarray | list[float], digits: int = 3) -> list[float]:
    arr = np.asarray(values, dtype=np.float64)
    return [float(round(float(v), digits)) for v in np.nan_to_num(arr)]


def sigmoid(x: float) -> float:
    if x >= 0:
        ez = np.exp(-x)
        return float(1.0 / (1.0 + ez))
    ez = np.exp(x)
    return float(ez / (1.0 + ez))
