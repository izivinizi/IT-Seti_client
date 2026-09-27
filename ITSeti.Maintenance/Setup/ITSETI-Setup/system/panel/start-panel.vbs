' start-panel.vbs - refreshes the support IDs, then starts the Desktop Info panel.
Set sh = CreateObject("WScript.Shell")
sh.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -File ""C:\ProgramData\ITSETI\update-support-ids.ps1""", 0, True
sh.Run """C:\Program Files\Desktop Info\DesktopInfo.exe"" ""C:\ProgramData\ITSETI\DesktopInfo.ini""", 0, False