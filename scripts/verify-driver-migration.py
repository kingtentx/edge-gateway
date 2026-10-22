#!/usr/bin/env python3
"""Verify the checked-in upstream migration inventory without fetching the network."""
from pathlib import Path
import json
import re
import sys
import xml.etree.ElementTree as ET


def main() -> int:
    root = Path(__file__).resolve().parents[1]
    manifest = json.loads((root / "docs/driver-migration-manifest.json").read_text(encoding="utf-8"))
    failures = []
    solution = (root / "EdgeGateway.sln").read_text(encoding="utf-8-sig").replace("\\", "/")
    for entry in manifest["sourceFiles"]:
        path = root / entry["path"]
        if not path.is_file() or path.stat().st_size == 0:
            failures.append(f"Missing or empty migrated source: {entry['path']}")

    for driver in manifest["drivers"]:
        project = root / driver["project"]
        if not project.exists():
            continue
        framework = ET.parse(project).getroot().findtext(".//TargetFramework")
        if framework != "net8.0":
            failures.append(f"{driver['directory']}: expected net8.0, got {framework}")
        if driver["project"] not in solution:
            failures.append(f"Project is not in the solution: {driver['project']}")
        code = "\n".join(p.read_text(encoding="utf-8-sig") for p in project.parent.glob("Device*.cs"))
        methods = set(re.findall(r"\[Method\([^\]]*\)\]\s*public\s+DriverReturnValueModel\s+(\w+)\(", code, re.S))
        parameters = set(re.findall(r"\[ConfigParameter\([^\]]*\)\]\s*public\s+\S+\s+(\w+)\s*\{", code, re.S))
        for method in driver["readMethods"]:
            if method not in methods:
                failures.append(f"{driver['directory']}: lost original read method {method}")
        for parameter in driver["configurationParameters"]:
            if parameter not in parameters:
                failures.append(f"{driver['directory']}: lost original parameter {parameter}")

    if failures:
        print("Driver migration inventory FAILED:", file=sys.stderr)
        for failure in failures:
            print(" - " + failure, file=sys.stderr)
        return 1
    print(f"Driver inventory verified: {len(manifest['drivers'])} projects, "
          f"{sum(len(d['readMethods']) for d in manifest['drivers'])} original read methods, "
          f"{sum(len(d['configurationParameters']) for d in manifest['drivers'])} original parameters, "
          f"{len(manifest['sourceFiles'])} source files.")
    print("This checks migration completeness; protocol operation is covered separately by tests and field acceptance.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
