@echo off
REM ============================================================================
REM  Build and deploy to Azure Static Web Apps.
REM
REM  Usage:
REM    publish-swa.bat <deployment-token>
REM
REM  Get the deployment token from:
REM    Azure Portal -> SWA resource -> Overview -> Manage deployment token
REM
REM  Or set the environment variable before running:
REM    set SWA_CLI_DEPLOYMENT_TOKEN=<token>
REM    publish-swa.bat
REM
REM  Prerequisites:
REM    npm install -g @azure/static-web-apps-cli
REM ============================================================================
setlocal

set "PROJ=%~dp0AzureMoe.Chat.Web.csproj"
set "OUT=%~dp0publish"
set "WWW=%OUT%\wwwroot"
set "API=%~dp0api"

REM Deployment token: argument 1 takes priority, then environment variable.
if not "%~1"=="" set "SWA_CLI_DEPLOYMENT_TOKEN=%~1"
if "%SWA_CLI_DEPLOYMENT_TOKEN%"=="" (
  echo ERROR: Deployment token not set.
  echo   publish-swa.bat ^<token^>
  echo   -- or --
  echo   set SWA_CLI_DEPLOYMENT_TOKEN=^<token^>
  exit /b 1
)

echo.
echo === [1/4] dotnet publish (Release, no AOT) ===
if exist "%OUT%" rmdir /s /q "%OUT%"
if exist "%~dp0obj\Release" rmdir /s /q "%~dp0obj\Release"
dotnet publish "%PROJ%" -c Release -o "%OUT%" -p:RunAOTCompilation=false
if errorlevel 1 (
  echo *** publish FAILED ***
  exit /b 1
)

echo.
echo === [2/4] Post-publish cleanup ===
del /s /q "%WWW%\*.br" >nul 2>&1
del /s /q "%WWW%\*.gz" >nul 2>&1
copy /y "%WWW%\index.html" "%WWW%\404.html" >nul

echo.
echo === [3/3] Deploying to Azure Static Web Apps ===
call swa deploy "%WWW%" --api-location "%API%" --deployment-token "%SWA_CLI_DEPLOYMENT_TOKEN%" --env production --api-language node --api-version 18 --verbose silly
if errorlevel 1 (
  echo *** swa deploy FAILED ***
  exit /b 1
)

echo.
echo ============================================================================
echo  DONE
echo ============================================================================

endlocal
