"""obs-websocket v5 authentication string."""

from __future__ import annotations

import base64
import hashlib

from clipforge_companion.obs import compute_authentication
from tests.fake_obs import expected_auth

PASSWORD = "supersecretpassword"
SALT = "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI="
CHALLENGE = "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY="
# Computed independently with .NET (PowerShell 5.1):
#   $sha = [Security.Cryptography.SHA256]::Create(); $utf8 = [Text.Encoding]::UTF8
#   $secret = [Convert]::ToBase64String($sha.ComputeHash($utf8.GetBytes($p + $salt)))
#   [Convert]::ToBase64String($sha.ComputeHash($utf8.GetBytes($secret + $challenge)))
EXPECTED_SECRET = "H1IfVz1pSREUQzbFTVnX/Tyb+gMhMik5x7yUBCY0PTs="
EXPECTED_AUTH = "1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4="


def test_authentication_matches_independent_dotnet_value() -> None:
    assert compute_authentication(PASSWORD, SALT, CHALLENGE) == EXPECTED_AUTH


def test_intermediate_secret_matches_documented_formula() -> None:
    secret = base64.b64encode(hashlib.sha256((PASSWORD + SALT).encode()).digest()).decode()
    assert secret == EXPECTED_SECRET


def test_authentication_matches_second_implementation_for_unicode_password() -> None:
    password, salt, challenge = "senha-çã-forte", "c2FsdA==", "Y2hhbGxlbmdl"
    assert compute_authentication(password, salt, challenge) == expected_auth(
        password, salt, challenge
    )


def test_authentication_depends_on_every_input() -> None:
    base = compute_authentication(PASSWORD, SALT, CHALLENGE)
    assert compute_authentication(PASSWORD + "x", SALT, CHALLENGE) != base
    assert compute_authentication(PASSWORD, SALT + "x", CHALLENGE) != base
    assert compute_authentication(PASSWORD, SALT, CHALLENGE + "x") != base
