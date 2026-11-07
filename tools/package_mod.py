#!/usr/bin/env python3
"""Create an installable ZIP after compiling PublicTransit.dll."""
import argparse
from pathlib import Path
import zipfile

ROOT = Path(__file__).resolve().parents[1]
INCLUDE = ("About", "Defs", "Textures", "Languages")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", default="dev", help="ZIP version identifier")
    args = parser.parse_args()
    dll = ROOT / "1.6" / "Assemblies" / "PublicTransit.dll"
    if not dll.is_file():
        parser.error(f"Build DLL first: {dll}")

    destination = ROOT / "dist" / f"PublicTransit-{args.version}.zip"
    destination.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED) as output:
        for subdir in INCLUDE:
            for file in sorted((ROOT / subdir).rglob("*")):
                if file.is_file():
                    relative = file.relative_to(ROOT)
                    output.write(file, f"PublicTransit/{relative.as_posix()}")
        output.write(dll, "PublicTransit/1.6/Assemblies/PublicTransit.dll")
    print(destination)


if __name__ == "__main__":
    main()
