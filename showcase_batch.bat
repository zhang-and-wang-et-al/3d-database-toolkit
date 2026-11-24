@echo off
rem Command line version, e.g.:  showcase_batch.bat "\\server\share\models" --preset quick --jobs 4
setlocal
set "PY="
py -3 -c "import sys" >nul 2>nul && set "PY=py -3"
if not defined PY python -c "import sys" >nul 2>nul && set "PY=python"
if not defined PY ( echo Python 3 not found - run "Start Toolkit (Windows).bat" first. & exit /b 1 )
%PY% "%~dp0setup_env.py" --run batch -- %*
