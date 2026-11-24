@echo off
rem Double-click to start 3D Database Toolkit. First start: sets up a private Python
rem environment (.venv) next to this file. Only Blender must be installed separately.
setlocal
rem pushd (not cd) also works when this folder is on a network share (\\server\share)
pushd "%~dp0"
call :findpython
if not defined PY (
    echo Python was not found. Trying to install it with winget ...
    winget install -e --id Python.Python.3.12 --scope user --accept-package-agreements --accept-source-agreements
    call :findpython
)
if not defined PY (
    echo.
    echo Please install Python 3 from https://www.python.org/downloads/
    echo ^(tick "Add python.exe to PATH" in the installer^), then start this file again.
    start "" https://www.python.org/downloads/
    pause
    exit /b 1
)
%PY% setup_env.py --run gui
if errorlevel 1 pause
exit /b

:findpython
set "PY="
rem the "python.exe" Store alias exists even without Python - so test that it really runs
py -3 -c "import sys" >nul 2>nul && set "PY=py -3" && goto :eof
python -c "import sys" >nul 2>nul && set "PY=python" && goto :eof
for %%V in (313 312 311 310 39) do (
    if exist "%LOCALAPPDATA%\Programs\Python\Python%%V\python.exe" (
        set PY="%LOCALAPPDATA%\Programs\Python\Python%%V\python.exe"
        goto :eof
    )
)
goto :eof
