[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ScriptPath,
    [Parameter(Mandatory = $true)][string]$LogPath
)

$ErrorActionPreference = 'Stop'

try {
    # Keep all PowerShell streams visible in the console and copy them to the
    # build log. This is intentionally kept in a .ps1 file so cmd.exe never
    # has to parse PowerShell redirection operators such as *>&1.
    & $ScriptPath *>&1 | Tee-Object -FilePath $LogPath
    exit 0
} catch {
    $_ | Out-String | Tee-Object -FilePath $LogPath -Append
    exit 1
}
