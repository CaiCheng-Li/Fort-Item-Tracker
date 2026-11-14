@echo off
rem Starts Fort Item Tracker. The first run sets up Python packages in .venv.
cd /d "%~dp0"
if not exist ".venv\Scripts\python.exe" (
    echo First run: installing packages...
    py -3.12 -m venv .venv 2>nul || python -m venv .venv
    ".venv\Scripts\python.exe" -m pip install --quiet -r requirements.txt
)
".venv\Scripts\python.exe" tracker.py %*
pause
