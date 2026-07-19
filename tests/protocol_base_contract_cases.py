"""Shared fixtures for the external NzbDAV protocol-base contract tests."""

from __future__ import annotations


VALID_PROTOCOL_BASES = (
    ("https://nzbdav.example.test/protocol", "https://nzbdav.example.test/protocol"),
    ("https://nzbdav.example.test/protocol/", "https://nzbdav.example.test/protocol"),
    ("https://example.test/nzbdav/protocol", "https://example.test/nzbdav/protocol"),
    ("https://example.test/nzbdav/protocol/", "https://example.test/nzbdav/protocol"),
    ("http://localhost:3000/protocol/", "http://localhost:3000/protocol"),
    ("http://[::1]:3000/protocol/", "http://[::1]:3000/protocol"),
    ("https://example.test:1/protocol", "https://example.test:1/protocol"),
    ("https://example.test:65535/protocol/", "https://example.test:65535/protocol"),
    ("https://example.test/%6ezbdav/pro%74ocol/", "https://example.test/nzbdav/protocol"),
)


INVALID_PROTOCOL_BASES = (
    ("empty", ""),
    ("whitespace-only", " "),
    ("relative", "nzbdav/protocol"),
    ("root-relative", "/protocol"),
    ("scheme-relative", "//example.test/protocol"),
    ("non-http", "ftp://example.test/protocol"),
    ("missing-host", "https:///protocol"),
    ("username", "https://user@example.test/protocol"),
    ("password", "https://user:password@example.test/protocol"),
    ("invalid-port", "https://example.test:not-a-port/protocol"),
    ("zero-port", "https://example.test:0/protocol"),
    ("out-of-range-port", "https://example.test:65536/protocol"),
    ("query", "https://example.test/protocol?mode=version"),
    ("empty-query", "https://example.test/protocol?"),
    ("fragment", "https://example.test/protocol#fragment"),
    ("empty-fragment", "https://example.test/protocol#"),
    ("leading-whitespace", " https://example.test/protocol"),
    ("trailing-whitespace", "https://example.test/protocol "),
    ("embedded-whitespace", "https://example.test/nzb dav/protocol"),
    ("line-feed", "https://example.test/nzbdav\n/protocol"),
    ("carriage-return", "https://example.test/nzbdav\r/protocol"),
    ("nul", "https://example.test/nzbdav\x00/protocol"),
    ("delete-control", "https://example.test/nzbdav\x7f/protocol"),
    ("c1-control", "https://example.test/nzbdav\x85/protocol"),
    ("empty-segment", "https://example.test/nzbdav//protocol"),
    ("dot-segment", "https://example.test/./protocol"),
    ("dotdot-segment", "https://example.test/nzbdav/../protocol"),
    ("encoded-dot-segment", "https://example.test/%2e/protocol"),
    ("encoded-dotdot-segment", "https://example.test/%2e%2e/protocol"),
    ("encoded-slash", "https://example.test/nzbdav%2fescape/protocol"),
    ("encoded-backslash", "https://example.test/nzbdav%5cescape/protocol"),
    ("repeated-trailing-slash", "https://example.test/protocol//"),
    ("malformed-percent", "https://example.test/nzbdav%/protocol"),
    ("double-encoded", "https://example.test/%252e%252e/protocol"),
    ("remaining-percent", "https://example.test/%2525/protocol"),
    ("missing-final-segment", "https://example.test/nzbdav"),
    ("wrong-case", "https://example.test/Protocol"),
    ("wrong-name", "https://example.test/proto"),
    ("prefix-confusable-hyphen", "https://example.test/protocol-api"),
    ("prefix-confusable-dot", "https://example.test/protocol.evil"),
    ("suffix-confusable", "https://example.test/notprotocol"),
    ("invalid-prefix-character", "https://example.test/apps+media/protocol"),
)


SAFE_BASE_ERROR = "NzbDAV protocol base is invalid."
SAFE_PATH_ERROR = "NzbDAV protocol path is invalid."
