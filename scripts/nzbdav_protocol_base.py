"""Fail-closed normalization for the public NzbDAV protocol URL base."""

from __future__ import annotations

import re
import unicodedata
import urllib.parse


SAFE_PROTOCOL_BASE_ERROR = "NzbDAV protocol base is invalid."
_PREFIX_SEGMENT = re.compile(r"^[A-Za-z0-9._~-]+$")
_HEX_DIGITS = frozenset("0123456789abcdefABCDEF")


def normalize_nzbdav_protocol_base(value: str) -> str:
    """Return one absolute, decoded, no-trailing-slash ``.../protocol`` base.

    Rejections intentionally share one input-independent diagnostic so a
    credential-bearing or otherwise sensitive rejected value cannot leak.
    """

    if not isinstance(value, str) or not value:
        _invalid()
    if any(character.isspace() or unicodedata.category(character) == "Cc" for character in value):
        _invalid()
    if "\\" in value or "?" in value or "#" in value:
        _invalid()

    try:
        parsed = urllib.parse.urlsplit(value)
        hostname = parsed.hostname
        port = parsed.port
    except (TypeError, ValueError, UnicodeError):
        _invalid()

    if parsed.scheme.lower() not in {"http", "https"} or not hostname:
        _invalid()
    if port == 0:
        _invalid()
    if parsed.username is not None or parsed.password is not None:
        _invalid()
    if parsed.query or parsed.fragment or not parsed.path.startswith("/"):
        _invalid()
    if parsed.netloc.endswith(":") or "%" in parsed.netloc:
        _invalid()

    path = parsed.path
    if path.endswith("/"):
        path = path[:-1]
        if path.endswith("/"):
            _invalid()
    if "//" in path:
        _invalid()

    raw_segments = path[1:].split("/")
    if not raw_segments or any(not segment for segment in raw_segments):
        _invalid()

    segments = [_decode_segment(segment) for segment in raw_segments]
    if segments[-1] != "protocol":
        _invalid()
    if any(not _PREFIX_SEGMENT.fullmatch(segment) for segment in segments[:-1]):
        _invalid()

    canonical_path = "/" + "/".join(segments)
    return urllib.parse.urlunsplit((parsed.scheme.lower(), parsed.netloc, canonical_path, "", ""))


def _decode_segment(raw_segment: str) -> str:
    index = 0
    while index < len(raw_segment):
        if raw_segment[index] != "%":
            index += 1
            continue
        if (
            index + 2 >= len(raw_segment)
            or raw_segment[index + 1] not in _HEX_DIGITS
            or raw_segment[index + 2] not in _HEX_DIGITS
        ):
            _invalid()
        index += 3

    try:
        decoded = urllib.parse.unquote_to_bytes(raw_segment).decode("utf-8", errors="strict")
    except (UnicodeDecodeError, ValueError):
        _invalid()

    if not decoded or decoded in {".", ".."}:
        _invalid()
    if "%" in decoded or "/" in decoded or "\\" in decoded:
        _invalid()
    if any(character.isspace() or unicodedata.category(character) == "Cc" for character in decoded):
        _invalid()
    return decoded


def _invalid() -> None:
    raise ValueError(SAFE_PROTOCOL_BASE_ERROR) from None
