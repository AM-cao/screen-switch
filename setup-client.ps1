# ScreenSwitch: configure this computer's SSH client side for reaching the other computer
# (a Mac or another Windows PC), and set peer.host / peer.user in the installed config.json.
# "ScreenSwitchCli.exe setup" runs this for you. To run it by hand, use a NORMAL (non-elevated)
# PowerShell in your own login session, after install.cmd:
#   powershell -ExecutionPolicy Bypass -File setup-client.ps1 -PeerUser <user> -PeerHost <name>.local [-PeerIP <lan-ip>]
# (the old parameter names -MacUser, -MacAlias, -MacIP still work)
# - creates ~/.ssh/id_ed25519 if missing and prints the public key (the other computer must
#   authorise it: see the end of the output)
# - writes a Host block for -PeerHost, IPv4 only, accept-new host keys:
#     -PeerIP given: pins HostName to that IP (use this if a proxy/router breaks mDNS, or the
#       router hands the other computer a fixed lease).
#     -PeerIP omitted: no HostName line; the name itself is resolved by mDNS (<name>.local).
# - after writing, resolves the name and warns if it fails, or resolves into 198.18.0.0/15
#   (a fake-IP range some proxies, e.g. Clash/mihomo, use to intercept .local lookups)
# - sets peer.host / peer.user in config.json (old and v3 files alike): the one next to this
#   script if there is one, else %USERPROFILE%\ScreenSwitch\config.json
# - safe to re-run: any previous Host block for the same name is replaced, not duplicated;
#   ~/.ssh/config is backed up to ~/.ssh/config.screenswitch.bak before each rewrite
# -FromSetup (used by "ScreenSwitchCli.exe setup"): output in UTF-8, leave config.json alone
#   (setup writes it) and do not print the key (setup prints it with the command to run).
param(
    [Parameter(Mandatory = $true)] [Alias('MacUser')] [string]$PeerUser,
    [Alias('MacIP')] [string]$PeerIP,
    [Parameter(Mandatory = $true)] [Alias('MacAlias')] [string]$PeerHost,
    [switch]$FromSetup
)
$ErrorActionPreference = 'Stop'
if ($FromSetup) {
    # the caller reads this script's output as UTF-8; it runs us without a window, so this
    # changes no console the user can see
    try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
}

# True if $Address falls in 198.18.0.0/15 - the fake-IP range fake-IP-mode proxies
# (Clash/mihomo and similar) commonly use to intercept DNS lookups, including *.local.
function Test-FakeIpRange {
    param([Parameter(Mandatory = $true)] [Net.IPAddress]$Address)
    if ($Address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { return $false }
    $bytes = $Address.GetAddressBytes()
    return ($bytes[0] -eq 198 -and ($bytes[1] -eq 18 -or $bytes[1] -eq 19))
}

# Sets the string value of "<Name>": "..." in a JSON text and returns the new text; $null when
# the key is not there. The value is JSON-escaped; the replacement is not a regex pattern, so
# "$" in a value is kept as is.
function Set-JsonString {
    param(
        [Parameter(Mandatory = $true)] [string]$Text,
        [Parameter(Mandatory = $true)] [string]$Name,
        [Parameter(Mandatory = $true)] [AllowEmptyString()] [string]$Value
    )
    $pattern = '"' + [regex]::Escape($Name) + '"\s*:\s*"(?:[^"\\]|\\.)*"'
    if (-not [regex]::IsMatch($Text, $pattern)) { return $null }
    $escaped = $Value.Replace('\', '\\').Replace('"', '\"')
    $replacement = '"' + $Name + '": "' + $escaped + '"'
    return [regex]::Replace($Text, $pattern, [System.Text.RegularExpressions.MatchEvaluator] { param($m) $replacement })
}

$sshDir = Join-Path $env:USERPROFILE '.ssh'
if (-not (Test-Path $sshDir)) { New-Item -ItemType Directory -Path $sshDir | Out-Null }
$key = Join-Path $sshDir 'id_ed25519'
if (-not (Test-Path "$key.pub")) {
    & "$env:WINDIR\System32\OpenSSH\ssh-keygen.exe" -t ed25519 -f $key -N '""' -C "screen-switch@$env:COMPUTERNAME" | Out-Null
    Write-Output "generated new key $key"
}

$block = New-Object System.Collections.Generic.List[string]
$block.Add("Host $PeerHost")
if (-not [string]::IsNullOrWhiteSpace($PeerIP)) { $block.Add("  HostName $PeerIP") }
$block.Add("  User $PeerUser")
$block.Add("  AddressFamily inet")
$block.Add("  StrictHostKeyChecking accept-new")
$block.Add("  IdentityFile ~/.ssh/id_ed25519")
$block.Add("  ConnectTimeout 3")

$cfg = Join-Path $sshDir 'config'
$existing = @()
if (Test-Path $cfg) {
    Copy-Item -LiteralPath $cfg -Destination ($cfg + '.screenswitch.bak') -Force
    $existing = @(Get-Content -LiteralPath $cfg -Encoding UTF8)
}
# drop any previous block for this name, then append the fresh one
$out = @(); $skip = $false
foreach ($line in $existing) {
    if ($line -match '^Host\s+(.+)$') { $skip = ($Matches[1].Trim() -eq $PeerHost) }
    if (-not $skip) { $out += $line }
}
$out += $block
# UTF-8 without BOM: ssh would read a BOM as part of the first line, and ASCII would turn
# non-ASCII characters (e.g. a user name inside an IdentityFile path) into "?"
[IO.File]::WriteAllLines($cfg, [string[]]$out, (New-Object System.Text.UTF8Encoding($false)))
Write-Output "ssh config updated: $cfg"

# ---- sanity-check that the name actually resolves (mDNS, or a proxy fake-IP hijack) ----
$addresses = @()
try {
    $addresses = @([System.Net.Dns]::GetHostAddresses($PeerHost) | Where-Object { $_.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork })
} catch {
    $addresses = @()
}
$fakeHit = @($addresses | Where-Object { Test-FakeIpRange $_ })
if ($addresses.Count -eq 0) {
    Write-Output ("警告：无法解析 " + $PeerHost + "（.local 被代理 fake-IP 截走时也会解析失败或指向假地址）；需要在代理的 fake-ip-filter 里加 '+.local'，或者改用 -PeerIP 并在路由器上给对方电脑固定分配地址")
} elseif ($fakeHit.Count -gt 0) {
    Write-Output ("警告：" + $PeerHost + " 解析到 " + $fakeHit[0].ToString() + "，这是代理 fake-IP 段（198.18.0.0/15），说明 .local 被代理截走了；需要在代理的 fake-ip-filter 里加 '+.local'，或者改用 -PeerIP 并在路由器上给对方电脑固定分配地址")
}

if ($FromSetup) { return }

# config.json is UTF-8 without BOM and may hold non-ASCII names: read and write it as such
$json = Join-Path $PSScriptRoot 'config.json'
if (-not (Test-Path -LiteralPath $json)) { $json = Join-Path $env:USERPROFILE 'ScreenSwitch\config.json' }
if (Test-Path -LiteralPath $json) {
    $text = [IO.File]::ReadAllText($json, [Text.Encoding]::UTF8)
    $withHost = Set-JsonString -Text $text -Name 'host' -Value $PeerHost
    $withUser = $null
    if ($null -ne $withHost) { $withUser = Set-JsonString -Text $withHost -Name 'user' -Value $PeerUser }
    if ($null -ne $withUser) {
        [IO.File]::WriteAllText($json, $withUser, (New-Object System.Text.UTF8Encoding($false)))
        Write-Output "config.json peer set: host=$PeerHost user=$PeerUser (restart ScreenSwitch.exe to apply)"
    } else {
        Write-Output "$json has no peer.host / peer.user to fill in - run ScreenSwitchCli.exe setup instead"
    }
} else {
    Write-Output "config.json not found at $json - run install.cmd first, then re-run this script"
}

Write-Output ''
Write-Output "This computer's public key. The other computer has to authorise it:"
Write-Output '  Windows (elevated PowerShell): powershell -ExecutionPolicy Bypass -File setup-sshd.ps1 -PeerPubKey "<the line below>"'
Write-Output '  Mac (Terminal): ~/screenctl/screenctl.sh authorize "<the line below>"'
Get-Content "$key.pub"
