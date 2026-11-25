# Virtual desktop VM probe

MissionView does not build, package, or call this console project. An unbound build does not verify the native calls. The sole candidate is native x64 Windows 11 25H2 `26200.8117` ([KB5086672](https://support.microsoft.com/en-us/servicing/os/windows-11/2026/03/march-31-2026-kb5086672-os-builds-26200-8117-and-26100-8117-out-of-band)). Do not add a build based on a matching interface ID or a nearby update.

## ABI evidence

Microsoft's [public desktop manager](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ivirtualdesktopmanager) does not list desktops, return the active desktop, or switch desktops. The pinned [VirtualDesktopAccessor README](https://github.com/Ciantic/VirtualDesktopAccessor/blob/ceb648849dcc5760a7f4ed223308ffe3de0b51c6/README.md) reports testing `26200.8117`; it does not give per-call results for this probe. Its [interface declarations](https://github.com/Ciantic/VirtualDesktopAccessor/blob/ceb648849dcc5760a7f4ed223308ffe3de0b51c6/src/interfaces.rs) and [call sites](https://github.com/Ciantic/VirtualDesktopAccessor/blob/ceb648849dcc5760a7f4ed223308ffe3de0b51c6/src/comobjects.rs) give the native ABI below. The pinned [MScholtes 24H2 declarations](https://github.com/MScholtes/VirtualDesktop/blob/a725cbd3cdb9e977678eeaf034a7cc96d2e74bc6/VirtualDesktop11-24H2.cs) independently agree on these early slots, but do not validate this build. The probe uses its own implementation, without upstream binaries or retry logic.

Slots are zero-based and include the three `IUnknown` entries. Methods take the interface pointer first and return `HRESULT`.

| Receiver | Slot | Operation and remaining arguments |
|---|---:|---|
| `IServiceProvider` | 3 | `QueryService(REFGUID, REFIID, void**)` |
| `IVirtualDesktopManagerInternal` | 4, 5 | `MoveViewToDesktop(IApplicationView*, IVirtualDesktop*)`, `CanViewMoveDesktops(IApplicationView*, BOOL*)` |
| `IVirtualDesktopManagerInternal` | 6 | `GetCurrentDesktop(IVirtualDesktop**)` |
| `IVirtualDesktopManagerInternal` | 7 | `GetDesktops(IObjectArray**)` |
| `IVirtualDesktopManagerInternal` | 9 | `SwitchDesktop(IVirtualDesktop*)` |
| [`IObjectArray`](https://learn.microsoft.com/en-us/windows/win32/api/objectarray/nn-objectarray-iobjectarray) | 3, 4 | `GetCount(UINT*)`, `GetAt(UINT, REFIID, void**)` |
| `IVirtualDesktop` | 4 | `GetId(GUID*)` |
| `IApplicationViewCollection` | 6 | `GetViewForHwnd(HWND, IApplicationView**)` |
| [`IVirtualDesktopManager`](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ivirtualdesktopmanager-getwindowdesktopid) | 4 | `GetWindowDesktopId(HWND, GUID*)` |

Use the private application-view route for a window owned by another app. Use the documented `GetWindowDesktopId` method only for before/after checks. The upstream cross-process move call site supports the proposed signature, not a claim that the move was tested on `26200.8117`.

## Run only after separate approval

Use an existing, disposable VM at the exact candidate revision. Take a revertible snapshot, isolate it from networks and shared folders, and use a local non-admin console session with stock Explorer and synthetic desktops. Use a preinstalled x64 .NET 10 SDK. Do not copy the resulting executable to the installed PC. For negative tests, use a separate disposable Windows x64 VM at a different revision. Do not update, downgrade, or restart the host.

Build inside the candidate guest. A default build leaves the COM path closed. For an authorized probe, bind the build to that guest from x64 PowerShell:

```powershell
dotnet build .\VirtualDesktopVm.csproj -o .\bin\disabled

$machineId = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Cryptography' -Name MachineGuid).MachineGuid
$sha = [System.Security.Cryptography.SHA256]::Create()
try {
    $binding = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($machineId))).Replace('-', '')
} finally {
    $sha.Dispose()
    Remove-Variable machineId
}
dotnet build .\VirtualDesktopVm.csproj -p:VmBinding=$binding -o .\bin\guest
Remove-Variable binding
```

Run these commands from the spike directory. Do not record the binding value or share the bound binary. Binding is an accidental-use guard, not VM detection or attestation. The probe also checks native x64, the exact kernel build and registry revision, an interactive session, and the Explorer shell generation before activating COM. It holds no COM objects between commands. The helper has no UI; bound each invocation from PowerShell:

```powershell
function Invoke-DesktopProbe($exe, [string[]]$probeArgs, [int]$expectedExit = 0) {
    $probe = Start-Process -FilePath $exe -ArgumentList $probeArgs -NoNewWindow -PassThru
    try {
        if (-not $probe.WaitForExit(10000)) {
            if (-not $probe.HasExited) { $probe.Kill() }
            throw 'Probe timed out. Treat any requested mutation as unconfirmed; do not retry.'
        }
        $code = $probe.ExitCode
        if ($code -notin 0, 2, 3) {
            throw "Probe exit: $code. Any requested mutation is unconfirmed; inspect Task View and do not retry."
        }
        if ($code -ne $expectedExit) {
            throw "Probe exit: $code (expected $expectedExit). Stop and review the probe output."
        }
        Write-Host "Probe exit: $code"
    } finally {
        $probe.Dispose()
    }
}

$disabled = '.\bin\disabled\VirtualDesktopVm.exe'
$guest = '.\bin\guest\VirtualDesktopVm.exe'
Invoke-DesktopProbe $disabled @('--vm-only', 'check') 3
Invoke-DesktopProbe $guest @('--vm-only', 'check')
Invoke-DesktopProbe $guest @('--vm-only', 'list')
# Set these two variables from the current list. Choose a non-active desktop.
Invoke-DesktopProbe $guest @('--vm-only', 'switch', '--allow-switch', $epoch, $target)
# For a separate move case, use a fresh list and a synthetic app window from the VM debugger.
Invoke-DesktopProbe $guest @('--vm-only', 'move', '--allow-move', $epoch, $hwndHex, $ownerPid, $source, $target)
```

For `move`, supply one `0x`-prefixed [HWND](https://learn.microsoft.com/en-us/windows/win32/winprog64/interprocess-communication), its decimal owner PID and distinct source and target GUIDs. You can use the 32-bit value or its zero- or sign-extended x64 form. Use an ordinary, unpinned top-level window from a separate non-admin test app. Keep the window open throughout the case. The HWND/PID/thread checks catch some stale selections, but cannot rule out handle reuse on the same thread; do not use this command as a product window identity mechanism. It does not activate the target desktop.

Exit `0` means the requested check, read, or confirmed result completed. Exit `2` means invalid arguments; `3` means the command stopped before changing state; `4` means the outcome is unconfirmed. Treat a crash, lost response, or timeout during either mutation as unconfirmed. Killing the worker does not cancel an Explorer-side RPC. Inspect Task View before any further experiment; do not replay the failed operation. Even a confirmed readback represents only that moment.

## Acceptance before product review

| Case | Expected evidence |
|---|---|
| Disabled, wrong guest and wrong revision | `check` and `list` stop with exit `3`. Verify the disabled build on the candidate guest, the candidate binary on a same-revision guest with a different machine identity if available, and the wrong-revision guest. An approved VM debugger breakpoint on `CoInitializeEx` in `DesktopAbi.Run` must not hit on these cases or on a passing `check`. A missing `--allow-switch` exits `2`. |
| List and active | Use Task View to create three desktops, including an empty one. Compare count, order and active marker with Task View. Reorder a desktop manually; the next list shows the new order and the same GUID for that desktop. |
| Switch and readback | Select the empty desktop by GUID. Expect one `SwitchDesktop` call, matching readback, and the correct active desktop in Task View. Requesting the already-active GUID is a no-op. Remove a non-active target via Task View before issuing a command using its old GUID; it must stop with exit `3`. |
| Move and readback | Put two synthetic app windows on the source desktop. Move one by HWND to an existing empty desktop. Expect one `MoveViewToDesktop` call, matching readback and exactly one window in the destination in Task View; the other window stays on the source and the active desktop does not change. |
| Move refusal | Omit `--allow-move` or pass identical source/target GUIDs: exit `2`. Pass a wrong PID, a closed window, an incorrect expected source, a missing target, or a stale Explorer token: exit `3` and no `MoveViewToDesktop` call. |
| Explorer restart | Save an epoch from `list`; restart Explorer using Task Manager in the guest. The old epoch must fail with exit `3` even if desktop GUIDs persist. Take a new read-only list once Explorer is healthy. For an in-flight check, pause the worker in a debugger before the final pre-switch `shell.Verify()`, restart Explorer in the guest, and resume before the watchdog expires. Expect exit `3` and no switch. |
| Lost response and deadline | In an approved debugger session, pause the worker after `SwitchDesktop` or `MoveViewToDesktop` returns but before readback and leave it stopped past the watchdog. Confirm the watchdog ends the worker without starting another and reports an unconfirmed outcome. Inspect Task View by hand. |

Stop on an HRESULT, crash, mismatch or Explorer incident and preserve the snapshot for review. Record build and revision, architecture, Explorer file version, source revision, case, exit code, HRESULT if applicable, and Task View agreement. Use redacted desktop labels in shared results. The probe prints desktop GUIDs and a shell-generation token to the local console; it does not enumerate windows, read their metadata, or persist telemetry. Even passing VM results do not authorize a product strip, settings, or a wider build allowlist.

## Future MissionView integration (design only)

[Task View (`Win+Tab`)](https://support.microsoft.com/en-us/windows/experience/configure-multiple-desktops-in-windows) is the manual path when the feature is off or unavailable. The documented desktop manager can classify a known window or request a window move; it cannot represent an empty desktop or switch to one. Do not infer the list from MissionView's window inventory, read Explorer's private registry state as an API, automate Task View, or inject shortcuts as a fallback. File transfer between windows is independent of this contract.

| Boundary | Minimum contract |
|---|---|
| Windows backend | `ReadAsync` returns the ordered desktop IDs, active ID and opaque Explorer generation, or an explicit unavailable reason. `SwitchAsync` accepts a desktop ID and the generation from the displayed snapshot. Validate the target again, issue at most one switch, and distinguish confirmed active, already active, rejected before dispatch and unconfirmed after dispatch. No names, HWNDs, thumbnails or window metadata cross the boundary. |
| Application | Keep one desktop state per overview; `OverviewSession` already spans displays. With the preference off, start no helper and read no desktop state. Discard responses after the overview closes or changes generation. Allow one switch in flight; on stale state or an unconfirmed outcome, invalidate the snapshot and permit only a fresh read, never a replay. On confirmation, dismiss stale window previews through the existing overview lifecycle; build a fresh inventory on the next open. |
| WPF | Render one logical strip above the previews in the overview's `MainWindow`/`OverviewChromeWindow` layer. Use numbered display labels, keyboard focus and an accessible active indicator; do not use labels or positions as identifiers. The strip stays hidden when off and offers no actionable targets after an unavailable result. The monitor filter still applies to previews, not to desktop switching. |

Window move is a separate capability: the application must pass a revalidated selection, expected source and target from the current snapshot, and require explicit input. After a confirmed move, refresh the window inventory rather than switching desktops. Keep window identity inputs out of the desktop list and persisted logs. Do not expose a move action until its own VM checks pass.

Keep COM and its deadline in a worker process, not on the WPF dispatcher. Canceling a UI wait does not cancel a request already sent to Explorer. Keep IDs and generation tokens in memory only. Do not wire this contract or add a user setting until exact-build VM evidence has been reviewed.

## Off-device route for `26200.9711`

Preferred: use an authorized existing lab VM or revertible image on another host, with a graphical hypervisor console. For Hyper-V, use [VMConnect Basic Session](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/enhanced-session-mode); Enhanced Session uses RDP. Keep the guest isolated and run from its active local console. This does not require enabling virtualization on the installed PC.

Microsoft's [October 2026 Marketplace image list](https://support.microsoft.com/en-us/servicing/os/azure-marketplace/windows-client/2026/windows-10-windows-11-client-images-for-october-2026) identifies x64 Pro and Enterprise images at `26200.9711.261011-1559`. That is a lead for locating an exact image, not proof of present availability, licensing, or a usable console. [Azure Serial Console](https://learn.microsoft.com/en-us/troubleshoot/azure/virtual-machines/windows/serial-console-windows) is text-only; guest RDP also does not meet the probe's active-console guard. Skip any provider that cannot supply a graphical console and a revertible guest at the exact revision.

For `26200.9711`, collect the guest edition, exact OS revision, Explorer and relevant shell module versions, and a pinned source or debugger-backed record of service/interface IDs, called slots and native signatures. A matching IID or successful activation is insufficient. Have that evidence reviewed before authorizing a separate guest-bound adaptation for this exact revision, then run the acceptance cases above against Task View and record the results. The existing `26200.8117` probe remains unchanged; a pass there cannot qualify `26200.9711`.
