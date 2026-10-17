# 2.4.11

## Windows 10/11 Client

- Installed diagnostics continue in the SYSTEM service after closing the UI.
- Service recovery has boot and 15-minute SYSTEM triggers, automatic startup and SCM restart actions.
- The service refreshes installed software from the server every five minutes and after diagnostics. Application installation waits for active operations to finish.
- Authenticated server version hints can trigger an earlier update check; missing hints keep the five-minute fallback. Installers are downloaded to Tools/Software and verified against server SHA-256 metadata.
- Defender status is read through the service. Policy operations report Windows rejection instead of reporting success without checking the result.
- Sensor names omit the source prefix, retain it in tooltips, and are grouped by type.
- The error-history view includes archived application errors, details and pagination. Failed automatic updates are logged; duplicate errors and secrets are filtered.
- Missing AnyDesk/RMS/OCS is visible in the engineer overview, including missing server installers.
- An authenticated engineer can remove unexpected local administrator membership; protected accounts and the last enabled local administrator are excluded.
- Automatic server assignment is saved locally. The server selects a single active site or an unambiguous same-company IP site; changing an address does not change the assignment ID.
- The installed diagnostic worker includes the password-policy helper in its run directory.

## Verification

- Release builds and PowerShell 5.1 parsing/encoding checks.
- Regression contracts: diagnostic rules, sensor labels/grouping, error archive/redaction, update metadata/hints, service concurrency, cancellation, stalled sensor reads, assignment UI and ticket notifications.
- Local controlled service stop followed by successful SYSTEM recovery.
- Local full diagnostics completed with the UI closed; DiskSpd results persisted.

## Boundaries

- This installer targets Windows 10/11 x64. Windows 7 remains a separate release; no real Windows 7 test is claimed.
- No real reboot was performed for this batch. Recovery triggers and controlled service restart were checked.
- Defender tamper protection and organization GPO/MDM are not bypassed.
- Removing an account from Administrators requires a new sign-in for existing access tokens to change.
- The original failure on remote computer 3586 has not been reproduced on that computer.
