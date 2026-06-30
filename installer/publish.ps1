<#
  Forge — empacotamento para o instalador.

  Publica o daemon (ForgeSentinel) e a UI (forge-ui) como payload self-contained
  win-x64 em installer\dist — sem exigir .NET instalado na máquina do usuário —
  e copia design\index.html + profiles\ ao lado dos executáveis (é onde o daemon
  os procura via LocateUi/LocateProfiles).

  Ambos os exes vão para a MESMA pasta de propósito: forge-ui acha o daemon
  buscando ForgeSentinel.exe ao seu lado (ver FindDaemonExe em MainForm.cs).

  Uso:
    pwsh installer\publish.ps1
  Depois compile installer\forge.iss no Inno Setup (ver installer\README.md).
#>
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot          # raiz do repo (installer\ está sob ela)
$dist = Join-Path $PSScriptRoot 'dist'

Write-Host "==> Limpando $dist" -ForegroundColor Cyan
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null

$common = @('-c','Release','-r','win-x64','--self-contained','true','-p:DebugType=none')

Write-Host "==> Publicando daemon (ForgeSentinel)" -ForegroundColor Cyan
dotnet publish (Join-Path $root 'spike-sentinel\ForgeSentinel.csproj') @common -o $dist
if ($LASTEXITCODE -ne 0) { throw "publish do daemon falhou ($LASTEXITCODE)" }

Write-Host "==> Publicando UI (forge-ui)" -ForegroundColor Cyan
dotnet publish (Join-Path $root 'forge-ui\ForgeUi.csproj') @common -o $dist
if ($LASTEXITCODE -ne 0) { throw "publish da UI falhou ($LASTEXITCODE)" }

Write-Host "==> Copiando design/ e profiles/" -ForegroundColor Cyan
New-Item -ItemType Directory -Force (Join-Path $dist 'design') | Out-Null
Copy-Item (Join-Path $root 'design\index.html') (Join-Path $dist 'design\index.html') -Force
Copy-Item (Join-Path $root 'profiles') (Join-Path $dist 'profiles') -Recurse -Force

# Sanidade: os dois exes têm que estar lado a lado no dist.
foreach ($exe in 'forge-ui.exe','ForgeSentinel.exe') {
  if (-not (Test-Path (Join-Path $dist $exe))) { throw "esperado $exe em dist, não encontrado" }
}

$size = '{0:N1} MB' -f ((Get-ChildItem $dist -Recurse | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "==> dist pronto ($size): $dist" -ForegroundColor Green
Write-Host "    Próximo: compile installer\forge.iss no Inno Setup." -ForegroundColor Green
