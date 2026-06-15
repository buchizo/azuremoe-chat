@echo off
REM ============================================================================
REM  Build the Cloudflare deploy artifact for AzureMoe.Chat.Web.
REM
REM  Steps:
REM    1. Clean previous publish output and obj\Release (so AOT relinks cleanly)
REM    2. dotnet publish -c Release  (AOT + trimming; needs wasm-tools workload)
REM    3. Delete pre-compressed .br/.gz  (Cloudflare compresses at the edge; the
REM       sibling files are dead weight and bloat the file count)
REM    4. Copy index.html -> 404.html  (SPA fallback; harmless even with the
REM       Worker's not_found_handling = single-page-application)
REM
REM  After this finishes, deploy with:   npx wrangler deploy
REM ============================================================================
setlocal

set "PROJ=%~dp0AzureMoe.Chat.Web.csproj"
set "OUT=%~dp0publish"
set "WWW=%OUT%\wwwroot"

echo.
echo === [1/4] Cleaning previous output ===
if exist "%OUT%" rmdir /s /q "%OUT%"
if exist "%~dp0obj\Release" rmdir /s /q "%~dp0obj\Release"

echo.
echo === [2/4] dotnet publish (Release, AOT) ===
dotnet publish "%PROJ%" -c Release -o "%OUT%"
if errorlevel 1 (
  echo.
  echo *** publish FAILED - aborting. ***
  exit /b 1
)

echo.
echo === [3/4] Removing pre-compressed .br/.gz ===
del /s /q "%WWW%\*.br" >nul 2>&1
del /s /q "%WWW%\*.gz" >nul 2>&1

echo.
echo === [4/4] Generating 404.html (SPA fallback) ===
copy /y "%WWW%\index.html" "%WWW%\404.html" >nul

REM --- summary ---
for /f %%C in ('dir /s /b /a-d "%WWW%" ^| find /c /v ""') do set "FILES=%%C"
echo.
echo ============================================================================
echo  Done. Artifact: %WWW%
echo  Files: %FILES%
echo  Deploy with:  npx wrangler deploy
echo ============================================================================

endlocal
