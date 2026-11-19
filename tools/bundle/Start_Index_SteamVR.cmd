@echo off
rem Valve Index with SteamVR. Starts the Multi-pass player; add SPI as an argument to start the SPI player.
call "%~dp0run_player.cmd" steamvr %*
