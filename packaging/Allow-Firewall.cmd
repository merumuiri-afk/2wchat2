@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Allow-Firewall.ps1" %*
