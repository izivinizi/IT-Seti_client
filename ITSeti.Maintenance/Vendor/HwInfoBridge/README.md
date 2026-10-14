# HWiNFO Shared Memory bridge

The shipped `ITSeti.HwInfoBridge.dll` is version 2.4.1.0. The private source remains under `.private/HwInfoBridge` on the build host and is not published with the product. `dotnet build .private/HwInfoBridge -c Release -o .build/hwinfo-2.4.1` produces the DLL and matching PDB; copy them into this directory before packaging.

The public bridge returns sensor name, unit, value, source Unix timestamp, sensor index and reading ID. `StartupState(pid)` emits only boolean/window-count diagnostics; it does not include registration text. `Read(mapName)` supports isolated memory-map fixtures; production calls `Read()` for `Global\HWiNFO_SENS_SM2`.
