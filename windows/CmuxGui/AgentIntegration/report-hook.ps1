param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('claude', 'codex')]
    [string]$Provider
)

try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw) -or $raw.Length -gt 65536) {
        exit 0
    }
    $inputEvent = $raw | ConvertFrom-Json
    $sessionId = [string]$inputEvent.session_id
    $eventName = [string]$inputEvent.hook_event_name
    if ([string]::IsNullOrWhiteSpace($sessionId) -or [string]::IsNullOrWhiteSpace($eventName)) {
        exit 0
    }

    $state = switch ($eventName) {
        'SessionStart' { 'idle' }
        'UserPromptSubmit' { 'working' }
        'Notification' { 'blocked' }
        'PermissionRequest' { 'blocked' }
        'Stop' { 'done' }
        'SessionEnd' { 'unknown' }
        default { 'unknown' }
    }
    if ($state -eq 'unknown') {
        exit 0
    }

    $payload = [pscustomobject]@{
        session_id = $sessionId
        state = $state
        cwd = [string]$inputEvent.cwd
        notify = $eventName -in @('Notification', 'PermissionRequest', 'Stop')
        resumable = $eventName -ne 'SessionEnd'
    } | ConvertTo-Json -Compress

    $gui = $env:CMUX_GUI_EXE
    $terminal = $env:CMUX_TERMINAL_ID
    if ([string]::IsNullOrWhiteSpace($gui) -or
        [string]::IsNullOrWhiteSpace($terminal) -or
        -not (Test-Path $gui -PathType Leaf)) {
        exit 0
    }
    $payload | & $gui --agent-hook $Provider $terminal
} catch {
}
exit 0
