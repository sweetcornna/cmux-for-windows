if ($args.Count -eq 0 -or @('claude', 'opencode', 'codex') -notcontains $args[0]) {
    Write-Error 'Expected claude, opencode, or codex as the first argument.'
    exit 64
}

$Provider = [string]$args[0]
$ProviderArguments = if ($args.Count -gt 1) { @($args[1..($args.Count - 1)]) } else { @() }

$shimDirectory = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$command = Get-Command $Provider -All -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Path -and
        -not [System.IO.Path]::GetFullPath($_.Path).StartsWith(
            "$shimDirectory\",
            [System.StringComparison]::OrdinalIgnoreCase)
    } |
    Select-Object -First 1

if (-not $command) {
    Write-Error "$Provider was not found after the cmux integration shim."
    exit 127
}

$arguments = [System.Collections.Generic.List[string]]::new()
foreach ($argument in $ProviderArguments) {
    $arguments.Add($argument)
}

$integration = $env:CMUX_AGENT_INTEGRATION_DIR
switch ($Provider) {
    'claude' {
        if ($integration) {
            $arguments.Insert(0, (Join-Path $integration 'claude'))
            $arguments.Insert(0, '--plugin-dir')
        }
    }
    'opencode' {
        if ($integration) {
            $plugin = ([System.Uri](Join-Path $integration 'opencode-plugin.js')).AbsoluteUri
            try {
                $config = if ([string]::IsNullOrWhiteSpace($env:OPENCODE_CONFIG_CONTENT)) {
                    [pscustomobject]@{}
                } else {
                    $env:OPENCODE_CONFIG_CONTENT | ConvertFrom-Json
                }
                $plugins = @($config.plugin)
                if ($plugins -notcontains $plugin) {
                    $plugins += $plugin
                }
                $config | Add-Member -NotePropertyName plugin -NotePropertyValue $plugins -Force
                $env:OPENCODE_CONFIG_CONTENT = $config | ConvertTo-Json -Depth 100 -Compress
            } catch {
                Write-Warning 'cmux could not merge its OpenCode status plugin; continuing without status integration.'
            }
        }
    }
    'codex' {
        if ($integration) {
            $hook = Join-Path $integration 'report-hook.ps1'
            $commandWindows = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$hook`" codex"
            $events = @('SessionStart', 'UserPromptSubmit', 'PermissionRequest', 'Stop', 'SessionEnd')
            $injected = [System.Collections.Generic.List[string]]::new()
            $injected.Add('--enable')
            $injected.Add('hooks')
            $injected.Add('--dangerously-bypass-hook-trust')
            foreach ($event in $events) {
                $escaped = $commandWindows.Replace('\', '\\').Replace('"', '\"')
                $injected.Add('-c')
                $injected.Add("hooks.$event=[{hooks=[{type=`"command`",command=`"$escaped`",commandWindows=`"$escaped`",timeout=10,async=true}]}]")
            }
            for ($index = $injected.Count - 1; $index -ge 0; $index--) {
                $arguments.Insert(0, $injected[$index])
            }
        }
    }
}

& $command.Path @arguments
exit $LASTEXITCODE
