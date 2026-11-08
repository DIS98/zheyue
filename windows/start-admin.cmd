@echo off
rem Run Clash Deck as administrator - required by TUN mode only.
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~dp0ClashDeck.exe' -Verb RunAs"
exit /b 0
