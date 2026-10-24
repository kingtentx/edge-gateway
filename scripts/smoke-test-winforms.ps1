[CmdletBinding()]
param(
    [string]$Executable = 'src/EdgeGateway.WinForms/bin/Release/net8.0-windows/EdgeGateway.WinForms.exe',
    [string]$OutputDirectory = 'artifacts',
    [ValidateRange(1, 3600)][int]$TimeoutSeconds = 900,
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    $outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    if (-not $VerifyOnly) {
        $applicationPath = [System.IO.Path]::GetFullPath($Executable)
        if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) { throw "WinForms executable not found: $applicationPath" }
        # Only the generated smoke family is replaced; stale screenshots must never
        # satisfy a new run. Preserve test results and published packages.
        Get-ChildItem -LiteralPath $outputRoot -Filter 'ui-smoke*' | Remove-Item -Recurse -Force
        $screenshot = Join-Path $outputRoot 'ui-smoke.png'
        $stdoutPath = Join-Path $outputRoot 'ui-smoke.stdout.log'
        $stderrPath = Join-Path $outputRoot 'ui-smoke.stderr.log'
        $process = Start-Process -FilePath $applicationPath -ArgumentList @('--smoke-test', '--layout-matrix', '--screenshot', ('"' + $screenshot + '"')) -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
        try {
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                $process.Kill($true)
                $process.WaitForExit()
                throw 'WinForms smoke test timed out.'
            }
            # Complete redirected stream handling before reading its evidence.
            $process.WaitForExit()
            if ($process.ExitCode -ne 0) {
                if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath }
                throw "WinForms smoke test failed: $($process.ExitCode)"
            }
        }
        finally { $process.Dispose() }
    }

    # Python is also required by verify-driver-migration.py. This standard-library
    # checker validates recorded evidence; it never fabricates or changes window DPI.
    $checker = @'
import hashlib
import json
import math
import os
from pathlib import Path
import re
import struct
import sys
import xml.etree.ElementTree as ET

repo, output = map(Path, sys.argv[1:3])
report_path = output / 'ui-smoke-layout-matrix/layout-matrix.json'
report = json.loads(report_path.read_text(encoding='utf-8-sig'))
stdout = (output / 'ui-smoke.stdout.log').read_text(encoding='utf-8-sig')
stderr = (output / 'ui-smoke.stderr.log').read_text(encoding='utf-8-sig')

def require(condition, message):
    if not condition:
        raise RuntimeError(message)

markers = [
    'WinForms editor smoke passed: 15 drivers',
    'Device configuration smoke passed: 15 dedicated driver windows',
    'Existing device driver change smoke passed:',
    'Device row entry smoke passed:',
    'Device enabled configuration smoke passed:',
    'Unified device dialog smoke passed:',
    'Dynamic driver switch passed at actual',
    'Device import smoke passed:',
    'Device-action/autosave smoke passed:',
    'Live configuration smoke passed: A kept collecting',
    'Completed capabilities smoke passed at actual',
    'Usability smoke passed at actual',
    'Layout matrix passed: 20 equivalent viewport cases',
]
for marker in markers:
    require(marker in stdout, 'Missing smoke completion marker: ' + marker)
require(not stderr.strip(), 'WinForms wrote errors: ' + stderr)
require(report['expectedCases'] == report['executedCases'] == report['passedCases'] == 20,
        'Layout matrix did not complete all 20 cases.')
require(report['viewportSimulation'] is True and report['nativeWindowResize'] is True,
        'Equivalent work-area verification must be identified as viewport simulation.')
require(report['taskbarReserveLogicalPixels'] == 48, 'Unexpected taskbar reserve in the viewport matrix.')
require(report['collectionRunning'] is False, 'Acquisition was left running at the end of layout verification.')
actual_dpi = report['actualDeviceDpi']
require(actual_dpi > 0 and actual_dpi == report['nativeWindowDpi'], 'Actual/native window DPI mismatch.')
capture_restores = report['screenshotVisibilityRestores']
require(report['screenshotVisibilityRestoreCount'] == len(capture_restores)
        == stdout.count('Screenshot visibility restored:'), 'Screenshot visibility restoration evidence is inconsistent.')
for restoration in capture_restores:
    before, captured, restored = (int(restoration[key], 16) for key in ('beforeStyle', 'capturedStyle', 'restoredStyle'))
    require(before == restored and before ^ captured == 0x10000000 and before & 0x10000000 != 0
            and restoration['context'], 'Screenshot restoration changed more than the recorded visibility side effect.')

pages = {'overview', 'devices', 'realtime', 'history', 'diagnostics', 'cloud', 'logs'}
dialogs = {'PLC.ModBusMaster device', 'PLC.ModBusMaster tag', 'WriteValueDialog multiline',
           'TagImportDialog preview', 'ChannelEditor'}
resolutions = [(1366, 768), (1920, 1080), (2560, 1440), (3200, 2000)]
percentages = [100, 125, 150, 175, 200]
case_specs = {f'{width}x{height}-{percent}pct': (width, height, percent)
              for width, height in resolutions for percent in percentages}
cases = report['cases']
require(len(cases) == 20 and {case['name'] for case in cases} == set(case_specs),
        'Layout case names are missing, duplicated, or unexpected.')
channel_names = {'ChannelName', 'ChannelDelay', 'ChannelDescription'}
write_names = {'WriteVerifyReadBack', 'WriteVerificationTimeout', 'WriteAbsoluteTolerance', 'WriteRelativeTolerance'}
metric_counts = {'headerMetrics': 3, 'deviceParameterMetrics': 12, 'channelInputMetrics': 3,
                 'writeVerificationMetrics': 4}

def check_metrics(values, count, context, names=None):
    require(len(values) == count, f'{context}: expected {count} control metrics, got {len(values)}.')
    if names is not None:
        require({value['name'] for value in values} == names, context + ': missing or unexpected control identities.')
    for value in values:
        require(value['actualDeviceDpi'] == actual_dpi, context + ': a control reported another actual DPI.')
        require(value['size']['width'] > 0 and value['size']['height'] > 0, context + ': empty control size.')
        require(math.isfinite(value['fontPoints']) and value['fontPoints'] > 0 and value['fontHeightPixels'] > 0,
                context + ': invalid font metrics.')

for case in cases:
    name = case['name']
    width, height, percent = case_specs[name]
    require(case['passed'] is True and case['error'] is None and case['viewportSimulation'] is True,
            name + ': case failed or did not declare viewport simulation.')
    require(case['physicalResolution']['width'] == width and case['physicalResolution']['height'] == height
            and case['nominalScalePercent'] == percent and case['nominalDpi'] == 96 * percent // 100,
            name + ': nominal resolution/scale metadata is inconsistent.')
    require(case['actualDeviceDpi'] == case['nativeWindowDpi'] == actual_dpi,
            name + ': actual DPI changed during viewport simulation.')
    require(case['actualWindowBounds'] == case['requestedWindowBounds'], name + ': requested/actual work areas differ.')
    logical_width, logical_height = width * 96.0 / case['nominalDpi'], height * 96.0 / case['nominalDpi'] - 48
    logical = case['equivalentLogicalWorkArea']
    require(math.isclose(logical['width'], logical_width, rel_tol=0, abs_tol=0.01)
            and math.isclose(logical['height'], logical_height, rel_tol=0, abs_tol=0.01),
            name + ': equivalent logical work area does not match the requested nominal pair.')
    require(case['requestedWindowBounds']['width'] == round(logical_width * actual_dpi / 96.0)
            and case['requestedWindowBounds']['height'] == round(logical_height * actual_dpi / 96.0),
            name + ': requested physical viewport does not match its unchanged actual DPI.')
    require(len(case['checkedPages']) == 7 and set(case['checkedPages']) == pages, name + ': not all seven pages were checked.')
    smallest = name == '1366x768-200pct'
    expected_dialogs = dialogs | ({'PLC.ModBusMaster dedicated configuration'} if smallest else set())
    require(len(case['checkedDialogs']) == len(expected_dialogs) and set(case['checkedDialogs']) == expected_dialogs,
            name + ': missing, duplicate, or unexpected dialog checks.')
    for key, count in metric_counts.items():
        names = {'channelInputMetrics': channel_names, 'writeVerificationMetrics': write_names}.get(key)
        check_metrics(case[key], count, name + ' ' + key, names)
    check_metrics(case['configurationParameterMetrics'], 12 if smallest else 0, name + ' minimum configuration')
    check_metrics([case['writeInputMetric']], 1, name + ' multiline write input', {'WriteValue'})

expected_images = {'ui-smoke.png'} | {f'ui-smoke-{page}.png' for page in pages - {'overview'}}
migration = json.loads((repo / 'docs/driver-migration-manifest.json').read_text(encoding='utf-8-sig'))
drivers = ['simulator', 'modbus-tcp', 'modbus-rtu'] + [driver['assembly'] for driver in migration['drivers']]
require(len(drivers) == 15 and len(set(drivers)) == 15, 'Expected all 15 built-in/migrated driver entries.')
for driver in drivers:
    identifier = re.sub(r'[^A-Za-z0-9_-]', '-', driver)
    expected_images.update(f'ui-smoke-{kind}-{identifier}.png' for kind in ('device', 'tag', 'configuration'))
expected_images.update('ui-smoke-' + name + '.png' for name in (
    'devices-running', 'devices-stopped', 'preset-modbus-rtu-com11', 'preset-configuration-modbus-rtu-com11',
    'tag-import-preview', 'live-configuration-devices', 'live-configuration-diagnostics', 'live-configuration-retained-draft',
    'usability-menu', 'usability-platform'))
for name in case_specs:
    expected_images.update(f'ui-smoke-layout-matrix/{name}-{kind}.png'
                          for kind in ('overview', 'modbus-device', 'channel'))
expected_images.update({'ui-smoke-layout-matrix/1920x1080-125pct-devices.png',
                        'ui-smoke-layout-matrix/1366x768-200pct-modbus-configuration.png'})
images = list(output.glob('ui-smoke*.png'))
for directory in output.glob('ui-smoke-*'):
    if directory.is_dir():
        images.extend(directory.rglob('*.png'))
actual_images = {image.relative_to(output).as_posix() for image in images}
require(len(expected_images) == 124, 'The expected screenshot manifest is internally inconsistent.')
require(actual_images == expected_images and len(images) == 124,
        f'Screenshot manifest mismatch: missing={sorted(expected_images - actual_images)}; unexpected={sorted(actual_images - expected_images)}.')
png_sizes = {}
for image in images:
    with image.open('rb') as stream:
        header = stream.read(24)
    require(len(header) == 24 and header[:8] == b'\x89PNG\r\n\x1a\n' and header[12:16] == b'IHDR',
            'Invalid PNG header: ' + str(image))
    size = struct.unpack('>II', header[16:24])
    require(all(value > 0 for value in size), 'Empty PNG: ' + str(image))
    png_sizes[image.relative_to(output).as_posix()] = size
for case in cases:
    bounds = case['actualWindowBounds']
    require(png_sizes[f"ui-smoke-layout-matrix/{case['name']}-overview.png"] == (bounds['width'], bounds['height']),
            case['name'] + ': overview PNG dimensions differ from the recorded actual window.')

test_results_summary = None
if os.environ.get('GITHUB_ACTIONS') == 'true':
    ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    tests = ET.parse(output / 'test-results/tests.trx')
    counters = tests.find('.//t:Counters', ns)
    require(counters is not None, 'The CI test report is missing result counters.')
    counts = {key: int(counters.attrib[key]) for key in ('total', 'passed', 'failed', 'notExecuted')}
    require(counts['total'] > 0 and counts['total'] == counts['passed']
            and counts['failed'] == counts['notExecuted'] == 0, 'The full CI test suite did not pass.')
    test_results_summary = counts

summary = dict(sourceCommit=os.environ.get('GITHUB_SHA'), workflowRunId=os.environ.get('GITHUB_RUN_ID'),
    actualDeviceDpi=actual_dpi, nativeWindowDpi=report['nativeWindowDpi'], viewportSimulation=True,
    layoutCasesPassed=20, layoutCasesExpected=20, checkedPages=sorted(pages), checkedDialogs=sorted(dialogs),
    screenshotCount=124, capabilitiesSmokePassed=True, usabilitySmokePassed=True,
    screenshotVisibilityRestoreCount=len(capture_restores),
    controlMetricCounts=metric_counts, markersPassed=markers, testResultsSummary=test_results_summary,
    reportSha256=hashlib.sha256(report_path.read_bytes()).hexdigest())
(output / 'ui-smoke-verification.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(f'Validated WinForms evidence: 7 pages, 5 dialog types, 20 equivalent work areas, 124 PNGs; actual {actual_dpi} DPI. Capability and usability smoke markers are present.')
'@
    $checker | & python - $repositoryRoot $outputRoot
    if ($LASTEXITCODE -ne 0) { throw 'WinForms evidence verification failed.' }
}
finally { Pop-Location }
