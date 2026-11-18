@echo off
rem Starts the full local glitch-port stack in Docker and opens the dashboard.
rem
rem   start-dashboard.bat          build if needed, start, open http://localhost:5173
rem   start-dashboard.bat stop     stop the stack (keeps nothing: data is disposable)
rem   start-dashboard.bat logs     follow the logs of every service
rem
rem Requires Docker Desktop with Compose v2. Ports: dashboard 5173, Relay 5080,
rem Processors 5091/5092, PostgreSQL 55432. Dev ingest DSN:
rem   http://dev-public-key@localhost:5080/1
setlocal
cd /d "%~dp0"

docker info >nul 2>&1
if errorlevel 1 (
    echo Docker is not running. Start Docker Desktop and try again.
    exit /b 1
)

if /i "%~1"=="stop" (
    docker compose --profile full down -v --remove-orphans
    exit /b %errorlevel%
)

if /i "%~1"=="logs" (
    docker compose --profile full logs -f
    exit /b %errorlevel%
)

echo Starting glitch-port (the first build can take several minutes)...
docker compose --profile full up -d --build --wait
if errorlevel 1 (
    echo.
    echo Startup failed. Recent logs:
    docker compose --profile full logs --tail 40
    exit /b 1
)

echo.
echo Dashboard: http://localhost:5173
echo Relay:     http://localhost:5080   (DSN http://dev-public-key@localhost:5080/1)
echo Stop with: start-dashboard.bat stop
start "" http://localhost:5173
endlocal
