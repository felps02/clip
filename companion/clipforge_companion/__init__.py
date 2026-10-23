"""Local OBS companion for live highlight detection.

The companion runs on the creator's own computer, follows the OBS recording file while it is
being written, slices it into segments with FFmpeg (stream copy, no re-encode) and uploads the
segments to the API with a device token. It never captures anything from streaming platforms.
"""

__version__ = "0.1.0"
