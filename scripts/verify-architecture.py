"""Check the compile-time boundaries of the desktop gateway without building it."""
from pathlib import Path
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
projects = list((ROOT / "src").rglob("*.csproj")) + list((ROOT / "Plugins").rglob("*.csproj"))
by_name = {path.stem: path for path in projects}
edges = {}
packages = {}
for project in projects:
    xml = ET.parse(project)
    edges[project.stem] = {Path(item.attrib["Include"].replace("\\", "/")).stem
                           for item in xml.findall(".//ProjectReference")}
    packages[project.stem] = {item.attrib["Include"] for item in xml.findall(".//PackageReference")}

expected = {
    "EdgeGateway.Model": set(),
    "EdgeGateway.Abstractions": {"EdgeGateway.Model"},
    "EdgeGateway.Service": {"EdgeGateway.Abstractions"},
    "EdgeGateway.Application": {"EdgeGateway.Service"},
    "EdgeGateway.DataAccess": {"EdgeGateway.Abstractions"},
    "EdgeGateway.Infrastructure.Mqtt": {"EdgeGateway.Service"},
    "EdgeGateway.Hosting": {"EdgeGateway.Application", "EdgeGateway.DataAccess", "EdgeGateway.Infrastructure.Mqtt",
                            "EdgeGateway.IotGateway", "EdgeGateway.Driver.Simulator", "EdgeGateway.Driver.Modbus"},
    "EdgeGateway.WinForms": {"EdgeGateway.Application", "EdgeGateway.Hosting"},
}
for name, references in expected.items():
    assert name in edges, f"Missing layer: {name}"
    assert edges[name] == references, f"Unexpected {name} dependencies: {edges[name]} (expected {references})"
assert not packages["EdgeGateway.Model"], "Domain models must not depend on third-party runtime implementations."
assert not packages["EdgeGateway.Service"], "The collection runtime must depend on contracts, not MQTT/SQL implementation packages."
assert {name for name, names in packages.items() if "MQTTnet" in names} == {"EdgeGateway.Infrastructure.Mqtt"}, \
    "MQTTnet belongs only to its infrastructure project."

for provider in ("MySqlConnector", "Npgsql", "Oracle.ManagedDataAccess.Core"):
    assert {name for name, names in packages.items() if provider in names} == {"EdgeGateway.DataAccess"}, \
        "Database provider packages belong only to DataAccess: " + provider

visited = set()
def visit(name, stack):
    assert name not in stack, "Project dependency cycle: " + " -> ".join([*stack, name])
    if name in visited:
        return
    assert name in edges, "Reference to an unknown project: " + name
    for dependency in edges[name]:
        visit(dependency, [*stack, name])
    visited.add(name)
for name in edges:
    visit(name, [])

for path in (ROOT / "src/EdgeGateway.WinForms").glob("*.cs"):
    text = path.read_text(encoding="utf-8-sig")
    assert not re.search(r"\b(?:GatewayRuntime|JsonConfigurationStore|SqliteGatewayStore|SqliteConfigurationStore|FileGatewayStore|ExternalDatabaseConnector|MqttTelemetryPublisher|IDriverFactory)\b", text), \
        "Presentation leaked an implementation or protocol factory: " + str(path.relative_to(ROOT))
    assert not re.search(r"using\s+EdgeGateway\.(?:Service|DataAccess|Infrastructure|IotGateway)\b", text), \
        "Presentation imports an infrastructure/runtime namespace: " + str(path.relative_to(ROOT))

contract = (ROOT / "src/EdgeGateway.Application/IGatewayApplication.cs").read_text(encoding="utf-8-sig")
for forbidden in ("GatewayRuntime", "IGatewayStore", "IConfigurationStore", "IDriverFactory", "ITelemetryPublisher"):
    assert not re.search(r"\b" + forbidden + r"\b", contract), "Application contract exposes implementation ownership: " + forbidden
for name in ("EdgeGateway.Application", "EdgeGateway.Hosting", "EdgeGateway.Infrastructure.Mqtt"):
    assert name + ".csproj" in (ROOT / "EdgeGateway.sln").read_text(encoding="utf-8-sig"), "Layer missing from solution: " + name
print(f"Architecture verified: {len(expected)} layer boundaries, {len(edges)} acyclic projects; UI/application isolation and MQTT package ownership passed.")
