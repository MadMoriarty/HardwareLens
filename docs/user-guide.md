# HardwareLens user guide

## First launch

Extract the portable ZIP and open `HardwareLens.exe`. Start without elevation. Hardware inventory can take a moment, and rate counters need at least two readings. Keep the app open during the workload you want to observe.

**Overview** combines current usage, grouped apps, hardware findings, and incident context. **Data & limits** lists the data sources, their status, limitations, and exact local file locations. Missing information can reflect unsupported hardware, driver limitations, or access denial.

## Find a busy app

Open **Apps**, search for a program, and inspect its grouped processes and details. CPU is a share of all logical processors; app memory is private memory and will not necessarily sum to the system's total used RAM. GPU process attribution depends on the counters Windows exposes.

Use a normal close action before force-ending a stuck program. Save work first: termination can discard unsaved data. HardwareLens rechecks process identity and refuses selected critical processes, but this is not a guarantee that every allowed action is safe. Priority changes last until the process exits; high priority can make other programs less responsive.

## Watch performance

Open **Performance** for CPU, memory, graphics, storage, and network history. Choose an interval and view window appropriate to the workload. The first sample after suspension establishes a baseline.

- **Pause display** freezes the view while collection continues.
- **Suspend collection** stops readings and leaves a gap in history.
- **Record** writes device and per-process CSV files with an info file. Stop recording when finished; the configured size limit also stops it.
- **Mini monitor** opens a compact always-on-top readout.

CSV recordings contain program names and usage. They are not encrypted. **Overview** can also save incident context automatically when a sustained condition is detected, even when CSV recording is off.

## Manage startup and services

**Startup** lists supported Run-key and Startup-folder entries. Disabling an entry changes its Windows StartupApproved flag rather than deleting its launch command. Enable it again to reverse the flag; effects generally apply at the next sign-in. Per-machine changes require elevation. This is not an inventory of every possible Windows autostart mechanism.

**Services** supports searching and filtering the service list. Start, stop, and restart require administrator rights in this UI and remain subject to Windows service permissions. Stop/restart prompts describe the selected service. HardwareLens refuses selected essential services; other services may still be necessary for networking, security, or your applications. Refresh after external changes. The UI does not offer a service startup-type editor.

Use **Restart as administrator** only when necessary and approve the Windows UAC prompt yourself. Some protected resources remain inaccessible even when elevated.

## Inspect hardware, storage, and network

**Hardware** shows the inventory Windows exposes. **Devices** shows device/driver details and problems. **Storage** shows volumes and supports folder-size investigation; inaccessible files can make totals incomplete. **Network** shows adapter information and traffic rates; these are interface totals, not packet capture or per-site browsing history.

**Observations** explains resource pressure and possible hardware issues. Review the measurement interval and missing data. Confirm suspected failures with Windows diagnostics or the device vendor's tools.

## Export and compare

Use **Export report** for an HTML report or JSON data. HTML can be opened in a browser; JSON is useful for structured review and comparison. Choose **Compare two exported JSON reports** to inspect changes between snapshots.

Serial numbers, MAC addresses, and computer name are withheld by default. **Include device identifiers** is an explicit option that is not persisted between app sessions. Other report content can still identify a machine indirectly. Review reports and recordings before sharing.

The **Open EventLens** shortcut launches the separate app if available; it is not required for HardwareLens monitoring.

## Shortcuts

Use **Keyboard shortcuts** or F1 for the current shortcut list. Navigation uses Ctrl+1 through Ctrl+9, then Ctrl+0 for Observations. Ctrl+E exports HTML, Ctrl+Shift+E exports JSON, and Ctrl+Shift+M toggles the mini monitor.

## Troubleshooting and removal

| Symptom | What to check |
| --- | --- |
| Blank or unavailable counter | Wait for baseline readings; inspect Data & limits and driver support. |
| Access denied | Check the selected action's requirements; try elevation only if justified. |
| GPU or drive temperature missing | Windows/driver may not expose it; elevation cannot add unsupported sensors. |
| Source build fails | Install the SDK in global.json and restore with NuGet connectivity. |
| Unexpected settings | Close the app and rename its settings file, then restart. |

Close the app and delete the portable folder to remove it. Also remove the fallback `%LOCALAPPDATA%\HardwareLens` folder if used. Preserve wanted recordings first. Removing the app does not undo startup-entry changes or restore services/processes you changed.
