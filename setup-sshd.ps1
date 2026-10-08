# ScreenSwitch: set up the Windows OpenSSH server so the other computer (a Mac or another Windows PC) can reach this one.
# Run in an elevated PowerShell:
#   powershell -ExecutionPolicy Bypass -File setup-sshd.ps1 -PeerPubKey "ssh-ed25519 AAAA... comment"
# (-PeerPubKey is the other computer's public key; the old name -MacPubKey still works)
#
# -PeerPubKey is optional after the first successful run:
#   - given: adds the key to administrators_authorized_keys, restricted to the ScreenSwitch
#     ssh-gate forced command. That file serves every administrator account on this PC, so
#     other keys already in it are kept; only an earlier ScreenSwitch ssh-gate line, or a line
#     with the same key, is replaced. The file is backed up once, before the first change, to
#     administrators_authorized_keys.screenswitch.bak.
#   - omitted and administrators_authorized_keys already exists: left untouched.
#   - omitted and administrators_authorized_keys does not exist: fails (first run needs it).
#
# Safety gates, all checked before anything is changed:
#   - must run elevated.
#   - at least one network connection must be categorized Private or DomainAuthenticated
#     (the firewall rule below is scoped to Private + LocalSubnet, never Any/Public, so
#     Windows stays unreachable from the internet even if a NIC is on a Public profile).
#     The "make it Private" hints name physical adapters only, never a virtual/TUN adapter
#     such as a proxy's Mihomo tunnel.
#   - administrators_authorized_keys must exist or -PeerPubKey must be supplied.
#
# Also hardens sshd_config (PasswordAuthentication no, KbdInteractiveAuthentication no,
# keys only) validated with "sshd -t"; on any failure the file is restored to exactly what it
# was right before this run (sshd_config.screenswitch.bak is only the one-time copy of the
# original, pre-ScreenSwitch config). On a fresh install, where sshd_config does not exist
# until sshd first starts, sshd is started once to generate it. Scopes the
# OpenSSH-Server-In-TCP firewall rule to Profile Private / RemoteAddress LocalSubnet.
# Installs the OpenSSH.Server capability, starts sshd, and adds the other computer's key to
# administrators_authorized_keys, restricted to the ScreenSwitch ssh-gate forced command.
# Note: PasswordAuthentication/KbdInteractiveAuthentication are turned off for every account,
# not only for ScreenSwitch.
param(
    [Alias('MacPubKey')] [string]$PeerPubKey
)
$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($id)
    return $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
}

# Ensures $Name is set to $Value in the global section of an sshd_config (i.e. before the
# first *uncommented* "Match" line): replaces the first uncommented directive with this
# name if one exists, else replaces the first commented-out one (e.g. "#PasswordAuthentication
# yes"), else inserts a new line right before the first Match block (or appends to the end
# if there is no Match block at all). Idempotent: running it twice with the same inputs
# yields the same output. Never touches the Match block(s) themselves.
function Set-SshdOption {
    param(
        # Not marked Mandatory: PowerShell's implicit "no empty string" check for Mandatory
        # parameters applies per-element to string[] too, and a real sshd_config's blank
        # lines would then throw on every call. $Name/$Value are always non-empty literals
        # from the caller below, so Mandatory is safe (and useful) for those two.
        [string[]]$Lines,
        [Parameter(Mandatory = $true)] [string]$Name,
        [Parameter(Mandatory = $true)] [string]$Value
    )
    if ($null -eq $Lines) { $Lines = @() }

    $matchIndex = $Lines.Length
    for ($i = 0; $i -lt $Lines.Length; $i++) {
        if ($Lines[$i].TrimStart() -match '^Match(\s|$)') { $matchIndex = $i; break }
    }

    $newLine = "$Name $Value"
    $escapedName = [regex]::Escape($Name)

    for ($i = 0; $i -lt $matchIndex; $i++) {
        if ($Lines[$i] -match "^\s*$escapedName\s+\S") {
            $Lines[$i] = $newLine
            return , $Lines
        }
    }

    for ($i = 0; $i -lt $matchIndex; $i++) {
        if ($Lines[$i] -match "^\s*#\s*$escapedName\s+\S") {
            $Lines[$i] = $newLine
            return , $Lines
        }
    }

    $result = New-Object System.Collections.Generic.List[string]
    if ($matchIndex -gt 0) { $result.AddRange([string[]]$Lines[0..($matchIndex - 1)]) }
    $result.Add($newLine)
    if ($matchIndex -lt $Lines.Length) { $result.AddRange([string[]]$Lines[$matchIndex..($Lines.Length - 1)]) }
    return , $result.ToArray()
}

# Lines to print about connections that are not Private/DomainAuthenticated (the sshd firewall
# rule only applies to Private). A "Set-NetConnectionProfile ... -NetworkCategory Private" hint
# is given only for physical adapters ($PhysicalAliases = names from Get-NetAdapter -Physical).
# Virtual adapters - a proxy's TUN such as Mihomo, Hyper-V/WSL switches, VPNs - are never
# suggested: making a proxy tunnel Private would open sshd on it. With -Blocking (no Private
# network at all, setup stops) they get a neutral "ignored" note, and a generic hint is added
# when no physical adapter qualifies; otherwise they are skipped silently.
function Get-NetworkCategoryHints {
    param(
        # Not Mandatory: an empty profile list is valid input.
        [object[]]$Profiles,
        [string[]]$PhysicalAliases,
        [switch]$Blocking
    )
    $physicalHints = 0
    foreach ($p in @($Profiles)) {
        if ($null -eq $p) { continue }
        if ($p.NetworkCategory -eq 'Private' -or $p.NetworkCategory -eq 'DomainAuthenticated') { continue }
        $alias = [string]$p.InterfaceAlias
        $isPhysical = ($null -ne $PhysicalAliases) -and ($PhysicalAliases -contains $alias)
        if ($isPhysical) {
            $physicalHints++
            if ($Blocking) {
                "提示：Set-NetConnectionProfile -InterfaceAlias '" + $alias + "' -NetworkCategory Private"
            } else {
                "提示：网络 '" + $p.Name + "' (" + $alias + ") 目前是 " + $p.NetworkCategory + "，防火墙规则只对 Private 生效；如果也要让它连对方电脑：Set-NetConnectionProfile -InterfaceAlias '" + $alias + "' -NetworkCategory Private"
            }
        } elseif ($Blocking) {
            "（" + $alias + " 不是物理网卡，已忽略）"
        }
    }
    if ($Blocking -and $physicalHints -eq 0) {
        '提示：请把连到对方电脑所在局域网的物理网卡（以太网 / WLAN）的网络设为 Private'
    }
}

# A fresh OpenSSH.Server install has no sshd_config yet: sshd writes its default one the first
# time it starts. If the file is missing, start sshd once ($StartSshd) and wait up to
# $TimeoutSeconds for it to appear, then stop sshd again ($StopSshd) so it does not keep serving
# with the default (password-enabled) settings before the hardened config is in place - the
# restart at the end of the script starts it again. Returns $null when the file exists, else
# the SETUP FAILED line to print (nothing in sshd_config has been changed in that case).
function Initialize-SshdConfig {
    param(
        [Parameter(Mandatory = $true)] [string]$ConfigPath,
        [Parameter(Mandatory = $true)] [scriptblock]$StartSshd,
        [Parameter(Mandatory = $true)] [scriptblock]$StopSshd,
        [int]$TimeoutSeconds = 10
    )
    if (Test-Path -LiteralPath $ConfigPath) { return $null }
    # Write-Host, not Write-Output: this function's output is its return value.
    Write-Host ('尚无 ' + $ConfigPath + '（全新安装），先启动一次 sshd 生成默认配置 ...')
    try { & $StartSshd | Out-Null } catch {
        return ('SETUP FAILED: 启动 sshd 以生成默认 sshd_config 失败 (' + $_ + ')，sshd_config 未修改')
    }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not (Test-Path -LiteralPath $ConfigPath) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    $created = Test-Path -LiteralPath $ConfigPath
    try { & $StopSshd | Out-Null } catch { Write-Host ('提示：暂停 sshd 失败 (' + $_ + ')，本脚本最后会重启它') }
    if (-not $created) {
        return ('SETUP FAILED: 启动 sshd 后 ' + $TimeoutSeconds + ' 秒内仍未生成 ' + $ConfigPath + '，sshd_config 未修改；请确认 sshd 能正常启动 (Get-Service sshd) 后重新运行本脚本')
    }
    return $null
}

# Hardens sshd_config (keys only) and validates it with "sshd -t". The file's exact bytes are
# read into memory before anything is written; on ANY failure in the write/validate window
# (WriteAllText failing partway, sshd.exe missing/access-denied and throwing instead of
# returning nonzero, a clean nonzero "sshd -t", ...) those bytes are written back, so a failed
# run leaves sshd_config exactly as this run found it - never modified and unvalidated, and
# never reverted to an older state. $BackupPath is only the one-time copy of the original,
# pre-ScreenSwitch config: created on the first run, never overwritten, not used for rollback.
# Returns $null on success, else the SETUP FAILED line to print.
function Update-SshdConfig {
    param(
        [Parameter(Mandatory = $true)] [string]$ConfigPath,
        [Parameter(Mandatory = $true)] [string]$BackupPath,
        [Parameter(Mandatory = $true)] [string]$SshdExe
    )
    $before = [IO.File]::ReadAllBytes($ConfigPath)
    if (-not (Test-Path -LiteralPath $BackupPath)) {
        Copy-Item -LiteralPath $ConfigPath -Destination $BackupPath
    }
    try {
        $lines = Get-Content -LiteralPath $ConfigPath
        $lines = Set-SshdOption -Lines $lines -Name 'PasswordAuthentication' -Value 'no'
        $lines = Set-SshdOption -Lines $lines -Name 'KbdInteractiveAuthentication' -Value 'no'
        $newText = ($lines -join "`r`n") + "`r`n"
        [IO.File]::WriteAllText($ConfigPath, $newText, (New-Object System.Text.UTF8Encoding($false)))

        # Out-Host: anything sshd prints must not become part of this function's return value.
        & $SshdExe -t | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "sshd -t exited with code $LASTEXITCODE" }
    } catch {
        $writeOrValidateError = $_
        try {
            [IO.File]::WriteAllBytes($ConfigPath, $before)
            return ('SETUP FAILED: sshd_config 写入/校验失败，已恢复为本次运行前的内容: ' + $writeOrValidateError)
        } catch {
            return ('SETUP FAILED: sshd_config 写入/校验失败 (' + $writeOrValidateError + ')，恢复为本次运行前的内容也失败 (' + $_ + ')；请手动修复 ' + $ConfigPath + '（首次运行前的原始配置备份在 ' + $BackupPath + '），然后重新运行本脚本')
        }
    }
    return $null
}

$logDir = Join-Path $env:USERPROFILE 'ScreenSwitch'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
$log = Join-Path $logDir 'setup-sshd.log'
Start-Transcript -Path $log -Force | Out-Null
try {
    if (-not (Test-IsAdministrator)) {
        Write-Output 'SETUP FAILED: 请在管理员 PowerShell 中运行'
        return
    }

    # ---- network category gate: need at least one Private/DomainAuthenticated connection ----
    $profiles = @(Get-NetConnectionProfile)
    # Physical adapter names, for the "make it Private" hints only (never a proxy TUN adapter).
    $physicalAliases = @()
    try { $physicalAliases = @(Get-NetAdapter -Physical | ForEach-Object { [string]$_.Name }) } catch { $physicalAliases = @() }
    $trusted = @($profiles | Where-Object { $_.NetworkCategory -eq 'Private' -or $_.NetworkCategory -eq 'DomainAuthenticated' })
    if ($trusted.Count -eq 0) {
        Write-Output '当前网络连接：'
        foreach ($p in $profiles) {
            Write-Output ('  ' + $p.Name + ' / ' + $p.InterfaceAlias + ' / ' + $p.NetworkCategory)
        }
        foreach ($l in @(Get-NetworkCategoryHints -Profiles $profiles -PhysicalAliases $physicalAliases -Blocking)) { Write-Output $l }
        Write-Output '（防火墙规则只对 Private 网络生效；把接口切到 Private 后重新运行本脚本）'
        Write-Output 'SETUP FAILED: 没有可用的专用(Private/DomainAuthenticated)网络，未做任何更改'
        return
    }
    foreach ($l in @(Get-NetworkCategoryHints -Profiles $profiles -PhysicalAliases $physicalAliases)) { Write-Output $l }

    # ---- authorized_keys precondition ----
    $ak = 'C:\ProgramData\ssh\administrators_authorized_keys'
    if ([string]::IsNullOrWhiteSpace($PeerPubKey) -and -not (Test-Path $ak)) {
        Write-Output 'SETUP FAILED: 首次运行需要 -PeerPubKey "<对方电脑的公钥>"'
        return
    }

    # ---- install / enable sshd ----
    $cap = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' | Select-Object -First 1
    if ($null -eq $cap) { throw 'OpenSSH.Server capability not available on this Windows image' }
    if ($cap.State -ne 'Installed') {
        Write-Output "Installing $($cap.Name) ..."
        Add-WindowsCapability -Online -Name $cap.Name | Out-Null
    } else { Write-Output "$($cap.Name) already installed" }

    # ---- sshd_config must exist (fresh install: generated by sshd's first start) ----
    $sshdConfigPath = 'C:\ProgramData\ssh\sshd_config'
    $initError = Initialize-SshdConfig -ConfigPath $sshdConfigPath -StartSshd { Start-Service sshd } -StopSshd { Stop-Service sshd }
    if ($null -ne $initError) {
        Write-Output $initError
        return
    }

    # ---- harden sshd_config: keys only, validated, restored to its pre-run content on ANY failure ----
    $backupPath = 'C:\ProgramData\ssh\sshd_config.screenswitch.bak'
    $sshdExe = Join-Path $env:WINDIR 'System32\OpenSSH\sshd.exe'
    $configError = Update-SshdConfig -ConfigPath $sshdConfigPath -BackupPath $backupPath -SshdExe $sshdExe
    if ($null -ne $configError) {
        Write-Output $configError
        return
    }

    # ---- authorized_keys ----
    # This file serves every administrator account, so keys that are not ours are kept: only an
    # earlier ScreenSwitch ssh-gate line, or a line carrying the same key, is replaced.
    if (-not [string]::IsNullOrWhiteSpace($PeerPubKey)) {
        $exe = Join-Path $env:USERPROFILE 'ScreenSwitch\ScreenSwitchCli.exe'
        $keyText = $PeerPubKey.Trim()
        $line = 'command="' + $exe + ' ssh-gate",no-port-forwarding,no-agent-forwarding,no-pty,no-X11-forwarding ' + $keyText
        # "<type> <base64>" identifies the key regardless of options and comment
        $keyParts = @($keyText -split '\s+')
        if ($keyParts.Length -ge 2) { $keyId = $keyParts[0] + ' ' + $keyParts[1] } else { $keyId = $keyText }
        $kept = New-Object System.Collections.Generic.List[string]
        if (Test-Path -LiteralPath $ak) {
            $akBackup = $ak + '.screenswitch.bak'
            if (-not (Test-Path -LiteralPath $akBackup)) { Copy-Item -LiteralPath $ak -Destination $akBackup }
            foreach ($l in @(Get-Content -LiteralPath $ak)) {
                if ([string]::IsNullOrWhiteSpace($l)) { continue }
                if ($l.Contains('ScreenSwitchCli.exe ssh-gate') -or $l.Contains($keyId)) { continue }
                $kept.Add($l)
            }
        }
        if ($kept.Count -gt 0) { Write-Output ('保留 ' + $ak + ' 里原有的 ' + $kept.Count + ' 行') }
        $kept.Add($line)
        [IO.File]::WriteAllText($ak, (($kept -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
        # SIDs instead of names: "Administrators" is localized on some Windows languages
        icacls $ak /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F' | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Output ('警告：icacls 设置 ' + $ak + ' 的权限失败（退出码 ' + $LASTEXITCODE + '），sshd 可能不会使用这个文件') }
    } else {
        Write-Output "保留已有的 $ak"
    }

    # ---- firewall: scoped to Private LAN only ----
    $ruleName = 'OpenSSH-Server-In-TCP'
    $rule = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
    if ($null -eq $rule) {
        New-NetFirewallRule -Name $ruleName -DisplayName 'OpenSSH Server (sshd)' -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 -Profile Private -RemoteAddress LocalSubnet | Out-Null
    } else {
        Set-NetFirewallRule -Name $ruleName -Enabled True -Profile Private -RemoteAddress LocalSubnet
    }

    $otherPort22 = New-Object System.Collections.Generic.List[object]
    $otherRules = @(Get-NetFirewallRule -Direction Inbound -Enabled True | Where-Object { $_.Name -ne $ruleName })
    foreach ($r in $otherRules) {
        $pf = $r | Get-NetFirewallPortFilter
        if ($pf.LocalPort -contains '22') { $otherPort22.Add($r) }
    }
    if ($otherPort22.Count -gt 0) {
        Write-Output '警告：以下已启用的入站规则也放行了 22 端口（未做修改）：'
        foreach ($r in $otherPort22) {
            Write-Output ('  ' + $r.Name + ' / ' + $r.DisplayName + ' / Profile=' + $r.Profile)
        }
    }

    # ---- restart + summary ----
    # sshd_config 加固和防火墙范围都已经成功才把 sshd 设为开机自启：中途失败退出时，一个密码登录仍
    # 打开的 sshd 不会被留成开机自启。
    Set-Service sshd -StartupType Automatic
    # Restart-Service can throw on a service that was never started (e.g. capability just
    # installed on this run), so it's not trusted alone: after it, explicitly verify Running
    # and Start-Service if needed, then fail loudly if it still isn't - the other computer cannot
    # connect over SSH without it.
    try { Restart-Service sshd } catch { Write-Output ('提示：Restart-Service 失败 (' + $_ + ')，尝试直接启动 sshd') }
    $svc = Get-Service sshd
    if ($svc.Status -ne 'Running') {
        Start-Service sshd
        $svc = Get-Service sshd
    }
    if ($svc.Status -ne 'Running') {
        Write-Output ('SETUP FAILED: sshd 未能启动 (当前状态: ' + $svc.Status + ')，对方电脑无法通过 SSH 连接到这台电脑')
        return
    }
    Write-Output ('sshd: ' + $svc.Status + ' / ' + $svc.StartType)
    $effective = & $sshdExe -T
    foreach ($l in $effective) {
        if ($l -match '^(passwordauthentication|kbdinteractiveauthentication)\s') { Write-Output ('sshd -T: ' + $l) }
    }
    $fwRule = Get-NetFirewallRule -Name $ruleName
    $fwAddr = Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $fwRule
    Write-Output ('firewall: profile=' + $fwRule.Profile + ' remoteaddress=' + ($fwAddr.RemoteAddress -join ','))
    Write-Output ('authorized_keys (ScreenSwitch): ' + (@(Get-Content $ak) | Where-Object { $_.Contains('ssh-gate') }))
    Write-Output 'SETUP OK'
} catch {
    Write-Output ('SETUP FAILED: ' + $_)
} finally {
    Stop-Transcript | Out-Null
}
