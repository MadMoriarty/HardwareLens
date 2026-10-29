# Permissions and privacy

## Windows access

The manifest requests `asInvoker`: normal launch does not request administrator access. Inventory and monitoring use local Windows facilities such as WMI/CIM, PDH performance counters, DXGI, process APIs, and device/network APIs. Permissions and driver support determine coverage.

| Operation | Access and effect |
| --- | --- |
| Inventory, charts, device information | Mostly standard-user reads; protected sources can be partial/unavailable. |
| End process / set priority | Changes the selected process; Windows enforces access, and protected targets can still refuse elevation. |
| Change startup entry | Writes StartupApproved flags; machine-wide entries need elevation. |
| Start/stop/restart service | UI requires elevation; service permissions still apply. |
| Folder scan | Reads files/directories within your access; cannot guarantee complete totals. |
| Reports and CSV | Writes files locally; no app-managed encryption. |

Management requires an explicit UI action. Critical-process and essential-service refusal lists reduce risk but do not cover every dependency or consequence.

## Local data

`hardwarelens.settings.json` and `recordings/` live beside the executable when writable, otherwise under `%LOCALAPPDATA%\HardwareLens`. Settings can include window state, preferences, and the last export folder. Data & limits displays the actual paths.

Incident context is saved automatically to `recordings/incidents.jsonl` when sustained observations arise. It can include program names, measurements, and timestamps. Appending stops after the file exceeds roughly 1 MB; it is not an unlimited audit log. Optional CSV recording has a separate configurable size limit and writes device/process readings and metadata.

Report exports exclude serial numbers, MAC addresses, and computer name unless explicitly enabled. This option does not anonymize every other field or apply to all local files. Software names, paths, volume labels, adapter details, and timing may still be private. Inspect any material before posting it publicly.

There is no cloud upload, account login, or telemetry pipeline in the app. Windows and installed providers may have their own behavior; the app's local data policy does not describe the whole operating system. Opening an external link or the companion application launches another program.

## Distribution and removal

Portable binaries are unsigned. Single-file .NET builds extract native runtime/graphics libraries to the normal per-user .NET temporary location. Do not describe these builds as signed or Microsoft-endorsed.

Close the app before deleting its portable and fallback data folders. This removes local app files, not the effects of management actions. Startup changes persist until reversed, and terminated work cannot be recovered by uninstalling the app.
