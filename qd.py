"""Quick dev install wrapper.

The old script copied a framework-dependent Debug build over the installed
Release application. That is no longer safe with the app/data/state layout, so
this now builds a self-contained Debug package and runs its setup in update
mode.
"""

import argparse
import subprocess
import sys
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser(description="Build a self-contained dev package and install it.")
    parser.add_argument(
        "--extension-id",
        default="",
        help="Chromium extension id to allow; defaults to the unpacked dev id in dev-install.ps1.",
    )
    args = parser.parse_args()

    script = Path(__file__).resolve().parent / "packaging" / "dev-install.ps1"
    if not script.is_file():
        print(f"Missing dev installer: {script}", file=sys.stderr)
        return 1

    command = [
        "powershell",
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        str(script),
    ]
    if args.extension_id:
        command += ["-ExtensionId", args.extension_id]

    result = subprocess.run(command, check=False)
    return result.returncode


if __name__ == "__main__":
    raise SystemExit(main())
