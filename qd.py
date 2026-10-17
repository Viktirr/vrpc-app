"""Quick dev install wrapper.

The old script copied a framework-dependent Debug build over the installed
Release application. That is no longer safe with the app/data/state layout, so
this now builds a self-contained Debug package and runs its setup in update
mode.
"""

import subprocess
import sys
from pathlib import Path


def main() -> int:
    script = Path(__file__).resolve().parent / "packaging" / "dev-install.ps1"
    if not script.is_file():
        print(f"Missing dev installer: {script}", file=sys.stderr)
        return 1

    result = subprocess.run(
        [
            "powershell",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(script),
        ],
        check=False,
    )
    return result.returncode


if __name__ == "__main__":
    raise SystemExit(main())
