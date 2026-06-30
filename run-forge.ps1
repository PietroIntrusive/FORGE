# Forge - launcher de teste.
# Sobe o daemon ELEVADO (faz o trabalho privilegiado: HttpListener + escrita no sistema)
# e a UI como usuario NORMAL. A UI detecta o daemon vivo e anexa. Modelo de seguranca:
# a UI nunca roda elevada -- por isso NAO rode este script "como administrador".
# Use o run-forge.cmd (clique duplo) ou: powershell -ExecutionPolicy Bypass -File run-forge.ps1

try {
    $root   = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $daemon = Join-Path $root 'spike-sentinel\bin\Release\net10.0-windows10.0.19041.0\ForgeSentinel.exe'
    $ui     = Join-Path $root 'forge-ui\bin\Release\net10.0-windows\forge-ui.exe'
    $status = 'http://127.0.0.1:5172/api/status'

    # Aviso se o usuario rodou elevado: a UI herdaria a elevacao (WebView2 pode quebrar).
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
             ).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
    if ($admin) {
        Write-Host "AVISO: voce rodou ELEVADO. A UI vai subir elevada (WebView2 pode falhar)." -ForegroundColor Yellow
        Write-Host "       Feche, e rode o run-forge.cmd com clique duplo normal (sem admin)." -ForegroundColor Yellow
        Write-Host ""
    }

    foreach ($p in @($daemon, $ui)) {
        if (-not (Test-Path $p)) {
            Write-Host "FALTA buildar: $p" -ForegroundColor Red
            Write-Host "Rode 'dotnet build -c Release' em spike-sentinel e forge-ui." -ForegroundColor Yellow
            return
        }
    }

    function Test-Daemon {
        try { return (Invoke-WebRequest $status -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 }
        catch { return $false }
    }

    if (Test-Daemon) {
        Write-Host "Daemon ja esta no ar (porta 5172)." -ForegroundColor Green
    } else {
        Write-Host "Subindo daemon elevado (aceite o UAC)..." -ForegroundColor Cyan
        try {
            Start-Process -FilePath $daemon -ArgumentList 'serve' -Verb RunAs -ErrorAction Stop
        } catch {
            Write-Host "UAC negado ou falhou: $($_.Exception.Message)" -ForegroundColor Red
            return
        }
        $ok = $false
        for ($i = 0; $i -lt 24 -and -not $ok; $i++) { Start-Sleep -Milliseconds 500; $ok = Test-Daemon }
        if (-not $ok) {
            Write-Host "Daemon nao respondeu a tempo. Porta 5172 ocupada? Antivirus barrou?" -ForegroundColor Red
            return
        }
        Write-Host "Daemon no ar." -ForegroundColor Green
    }

    Write-Host "Abrindo A Forja..." -ForegroundColor Cyan
    Start-Process -FilePath $ui
    Write-Host "Pronto. Fecha = bandeja. Encerrar de verdade: bandeja > Encerrar Forja." -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "ERRO: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
}
finally {
    Write-Host ""
    Read-Host "Enter para fechar"
}
