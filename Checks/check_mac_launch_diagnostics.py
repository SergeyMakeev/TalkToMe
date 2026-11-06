"""Refuse to upload launch diagnostics that resemble credentials or environment dumps."""
from pathlib import Path
import re
import sys


SENSITIVE = re.compile(
    rb"gh[pousr]_[A-Za-z0-9_.-]{16,}|github_pat_[A-Za-z0-9_]{16,}"
    rb"|eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}"
    rb"|\b(?:GH_TOKEN|GITHUB_TOKEN|ACTIONS_RUNTIME_TOKEN|ACTIONS_ID_TOKEN_REQUEST_TOKEN)\b"
)


def safe(data):
    return SENSITIVE.search(data) is None


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        assert safe(b"Signature=adhoc\nTeamIdentifier=not set\nMain Thread: _dyld_start")
        assert not safe(b'"GH_TOKEN" => { contents = "redacted" }')
        assert not safe(b"token=ghs_12345_" + b"A" * 30)
        assert not safe(b"token=github_pat_" + b"b" * 30)
        assert not safe(b"bearer eyJhbGciOiJSUzI1NiJ9.eyJhdWQiOiJhY3Rpb25zIn0.signature_example")
        print("Diagnostic credential checks passed.")
    else:
        root = Path(sys.argv[1])
        if not root.is_dir():
            sys.exit("No diagnostic directory to validate.")
        for path in root.rglob("*"):
            if path.is_file() and not safe(path.read_bytes()):
                # Never print the matching content, which may be a credential.
                sys.exit(f"Refusing diagnostic upload: sensitive content detected in {path.name}.")
        print("Diagnostic files checked; no credential patterns detected.")
