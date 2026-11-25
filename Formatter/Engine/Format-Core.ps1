# Nexus USB Formatter 1.6.0 - read-only Windows inventory; native owned formatter.
# Dot-sourcing this file defines functions only. It does not touch disks.
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$script:NexusStage='Startup'

function Set-NexusStage([string]$Message) {
    $script:NexusStage=$Message
    Send-Event 'stage' $Message
}
function Get-NexusErrorDetail($Record) {
    $command='(not supplied by Windows)'
    if($null -ne $Record.InvocationInfo -and $null -ne $Record.InvocationInfo.MyCommand) {
        $command=[string]$Record.InvocationInfo.MyCommand.Name
    }
    $details=@($Record.Exception.Message,('Command: '+$command),
        ('Error ID: '+[string]$Record.FullyQualifiedErrorId),('Category: '+[string]$Record.CategoryInfo.Category),
        ('Exception: '+$Record.Exception.GetType().FullName),('HRESULT: '+$Record.Exception.HResult))
    $exception=$Record.Exception
    while($null -ne $exception) {
        if($exception.PSObject.Properties['NativeErrorCode']) {$details+=('Native error: '+$exception.NativeErrorCode)}
        if($exception -ne $Record.Exception) {$details+=('Inner: '+$exception.Message)}
        $exception=$exception.InnerException
    }
    return $details -join [Environment]::NewLine
}
function Import-NexusNative {
    if(-not ('NexusFormatter.NativeStorage' -as [type])) {
        Add-Type -Path @('NativeStorage.cs','NativePartition.cs','ManagedExFat.cs','ExFatUpcase.cs','NativeFormat.cs','ExFatConversion.cs','ConversionJournal.cs','PartitionConversion.cs','NativeConversion.cs' | ForEach-Object {Join-Path $PSScriptRoot $_}) -ErrorAction Stop
    }
}
function Get-NexusRecoveryHint($Record) {
    $detail=Get-NexusErrorDetail $Record
    if($detail -match 'Native error: 19\b|Win32 19\b|write.protect|read.only') {
        return 'The drive is write-protected. Check its physical lock and adapter. The formatter will not override write protection; a failing drive can also become permanently read-only.'
    }
    if($detail -match 'Native error: (32|33)\b|Win32 (32|33)\b|sharing violation|being used') {
        return 'Windows reports the drive is in use. Close Explorer windows, backup/sync apps and other disk tools, then reconnect, Refresh and reselect this drive. No processes will be forcibly closed.'
    }
    if($detail -match 'Native error: (21|1167)\b|Win32 (21|1167)\b|missing or ambiguous|not ready|disconnected') {
        return 'The selected drive is no longer ready. Check its cable, USB port and power; reconnect it, then Refresh and reselect it. No replacement disk will be used automatically.'
    }
    if($detail -match 'Access.*denied|UnauthorizedAccess|PermissionDenied|Native error: (5|1314)\b|0x80070005|HRESULT: -2147024891') {
        return 'Windows refused disk access. Run this app from the PC and accept its administrator prompt. Close apps using the USB drive. If it still fails, check removable-storage restrictions with your administrator and send the saved log using Open logs. Security protections are not bypassed.'
    }
    return 'The drive has not been certified ready. Keep the saved log (Open logs). Check the connection, then Refresh and reselect before another deliberate attempt.'
}
function Enable-NexusDiskAccess {
    Import-NexusNative
    [NexusFormatter.NativeStorage]::EnableDiskPrivilege()
}
function Send-Event([string]$Kind,[string]$Message,$Data=$null) {
    [Console]::Out.WriteLine((@{kind=$Kind;message=$Message;data=$Data} | ConvertTo-Json -Depth 12 -Compress))
}
function Get-ProtectedDisks([string[]]$ProtectedPaths) {
    # Fail closed if system/page-file/partition inventory cannot be read.
    $paths=@($env:SystemRoot,$env:USERPROFILE,$PSScriptRoot)+$ProtectedPaths
    $paths+=@(Get-CimInstance Win32_PageFileUsage -ErrorAction Stop | ForEach-Object { $_.Name })
    $partitions=@(Get-Partition -ErrorAction Stop)
    $numbers=New-Object 'System.Collections.Generic.HashSet[int]'
    foreach($p in $partitions) {
        if($p.IsBoot -or $p.IsSystem) { [void]$numbers.Add([int]$p.DiskNumber) }
        foreach($access in @($p.AccessPaths)) {
            if([string]::IsNullOrWhiteSpace($access)) {continue}
            foreach($path in $paths) {
                if([string]::IsNullOrWhiteSpace($path)) {continue}
                $full=[IO.Path]::GetFullPath($path).TrimEnd('\')+'\'
                if($full.StartsWith($access.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) {
                    [void]$numbers.Add([int]$p.DiskNumber)
                }
            }
        }
    }
    return @($numbers)
}
function Get-BlockReasons($Disk,$Partitions,[int[]]$ProtectedNumbers) {
    $reasons=New-Object 'System.Collections.Generic.List[string]'
    if([string]$Disk.BusType -ne 'USB') {$reasons.Add('Not a directly identified USB disk.')}
    if($Disk.IsBoot -or $Disk.IsSystem -or @($Partitions | Where-Object {$_.IsBoot -or $_.IsSystem}).Count) {$reasons.Add('Windows boot/system disk.')}
    if($ProtectedNumbers -contains [int]$Disk.Number) {$reasons.Add('Contains Windows, a page file, this app, its logs or your profile. Run the app from the PC.')}
    if($Disk.IsOffline) {$reasons.Add('Disk is offline.')}
    if($Disk.IsReadOnly) {$reasons.Add('Disk is write-protected.')}
    if($Disk.IsClustered) {$reasons.Add('Cluster-managed disk.')}
    if([string]$Disk.HealthStatus -ne 'Healthy') {$reasons.Add('Disk health is not reported Healthy.')}
    if([uint64]$Disk.Size -lt 1GB) {$reasons.Add('At least 1 GiB is required by this formatter.')}
    if([int]$Disk.LogicalSectorSize -notin @(512,4096)) {$reasons.Add('Unsupported logical sector size.')}
    elseif(([uint64]$Disk.Size % [uint64]$Disk.LogicalSectorSize) -ne 0) {$reasons.Add('Disk capacity is not aligned to its logical sector size.')}
    if([string]::IsNullOrWhiteSpace([string]$Disk.Path) -or
       [string]::IsNullOrWhiteSpace([string]$Disk.SerialNumber)) {
        $reasons.Add('A stable disk path and hardware serial are required for native formatting.')
    }
    return @($reasons)
}
function Get-DiskSnapshot($Disk,[int[]]$ProtectedNumbers) {
    # Enumerate then filter so a RAW disk with no partitions is not treated as a
    # failed DiskNumber lookup by some versions of the Storage module.
    $parts=@(Get-Partition -ErrorAction Stop | Where-Object {$_.DiskNumber -eq $Disk.Number} | Sort-Object PartitionNumber)
    $reasons=@(Get-BlockReasons $Disk $parts $ProtectedNumbers)
    $details=@()
    foreach($p in $parts) {
        $volumes=@($p | Get-Volume -ErrorAction SilentlyContinue)
        $v=if($volumes.Count) {$volumes[0]} else {$null}
        $details+= [pscustomobject][ordered]@{
            number=[int]$p.PartitionNumber;offset=[uint64]$p.Offset;size=[uint64]$p.Size
            letter=if($p.DriveLetter -and [int][char]$p.DriveLetter -ge 65) {[string]$p.DriveLetter} else {''}
            label=if($null -ne $v) {[string]$v.FileSystemLabel} else {''}
            filesystem=if($null -ne $v) {[string]$v.FileSystem} else {''}
        }
        # A mods-only atmosphere directory is not a Switch SD marker. Require
        # the actual boot package file, including on volumes with no letter.
        foreach($access in @($p.AccessPaths)) {
            if([string]::IsNullOrWhiteSpace($access)) {continue}
            if(Test-Path -LiteralPath ([IO.Path]::Combine($access,'atmosphere','package3')) -PathType Leaf -ErrorAction Stop) {
                $reasons+= 'Possible Switch SD card (atmosphere/package3 file found). This formatter will not erase it.'
            }
        }
    }
    [pscustomobject][ordered]@{
        number=[int]$Disk.Number;path=[string]$Disk.Path;uniqueId=[string]$Disk.UniqueId
        serial=([string]$Disk.SerialNumber).Trim();model=([string]$Disk.FriendlyName).Trim()
        size=[uint64]$Disk.Size;sector=[int]$Disk.LogicalSectorSize
        style=[string]$Disk.PartitionStyle;offline=[bool]$Disk.IsOffline;partitions=@($details)
        recommendedProfile=(Get-RecommendedProfile ([uint64]$Disk.Size))
        allowed=($reasons.Count -eq 0);reasons=@($reasons | Select-Object -Unique)
    }
}
function Assert-Identity($Expected,$Disk) {
    # Number alone is never sufficient: it can be reused after unplugging.
    if([int]$Disk.Number -ne [int]$Expected.number -or
       [string]$Disk.Path -cne [string]$Expected.path -or
       ([string]$Disk.SerialNumber).Trim() -cne [string]$Expected.serial -or
       [string]$Disk.UniqueId -cne [string]$Expected.uniqueId -or
       ([string]$Disk.FriendlyName).Trim() -cne [string]$Expected.model -or
       [uint64]$Disk.Size -ne [uint64]$Expected.size -or
       [int]$Disk.LogicalSectorSize -ne [int]$Expected.sector -or [string]$Disk.BusType -ne 'USB') {
        throw 'Disk identity changed or the drive was reconnected. Refresh and select it again. Nothing further was written.'
    }
}
function Get-ConfirmedDisk($Expected,[string[]]$ProtectedPaths) {
    $matches=@(Get-Disk -ErrorAction Stop | Where-Object { [string]$_.Path -ceq [string]$Expected.path })
    if($matches.Count -ne 1) {throw 'The selected physical USB disk is missing or ambiguous.'}
    $d=$matches[0]
    Assert-Identity $Expected $d
    $parts=@(Get-Partition -ErrorAction Stop | Where-Object {$_.DiskNumber -eq $d.Number})
    $blocked=@(Get-BlockReasons $d $parts (Get-ProtectedDisks $ProtectedPaths))
    if($blocked.Count) {throw ($blocked -join ' ')}
    return $d
}
function Assert-Request($Request) {
    # The selected drive's Format button is the user action; no second dialog.
    if($Request.schema -ne 3 -or $Request.userInitiated -isnot [bool] -or $Request.userInitiated -ne $true -or
       [string]$Request.action -cne 'format-selected-usb') {throw 'A request from the selected-drive Format action is required.'}
    if([string]$Request.label -notmatch '^[A-Za-z0-9][A-Za-z0-9 _-]{0,10}\z' -or [string]$Request.label -ne ([string]$Request.label).Trim()) {
        throw 'Label must be 1-11 letters, numbers, spaces, underscores or hyphens; no leading/trailing space.'
    }
    $issued=[DateTime]::Parse([string]$Request.issuedUtc).ToUniversalTime()
    $age=([DateTime]::UtcNow-$issued).TotalSeconds
    if($age -lt -5 -or $age -gt 120) {throw 'Format request expired. Refresh the drive and select it again.'}
    if([string]::IsNullOrWhiteSpace([string]$Request.appPath) -or
       -not (Test-Path -LiteralPath ([string]$Request.appPath) -PathType Leaf)) {throw 'Cannot verify the formatter application location.'}
    Assert-Profile $Request
}
function Get-Profile([string]$Name) {
    switch -CaseSensitive ($Name) {
        'standard512' {return 'MBR'}
        'large512' {return 'GPT'}
        default {throw 'Unknown format profile. Refresh and select Standard or Large drive.'}
    }
}
function Get-RecommendedProfile([uint64]$Size) {
    # Conservative product policy in decimal TB, matching the displayed capacity.
    # This is not a claim that MBR's addressing limit is exactly 2 trillion bytes.
    if($Size -gt 2000000000000) {return 'large512'}
    return 'standard512'
}
function Test-LegacyLoaderLimit($Snapshot) {
    # Conservative check on whole-disk geometry, not a compatibility certificate.
    # Old loader builds reject partitions whose sector count exceeds DWORD.
    # L1/newer 64-bit USB builds removed that restriction; a PC formatter cannot
    # determine which loader is installed on the user's Switch.
    return [decimal]$Snapshot.size -gt ([decimal]4294967295 * [int]$Snapshot.sector)
}
function Assert-Profile($Request) {
    $style=Get-Profile ([string]$Request.profile)
    if($style -eq 'MBR' -and (Get-RecommendedProfile ([uint64]$Request.disk.size)) -eq 'large512') {
        throw 'Disks over 2 TB require the GPT profile in this formatter; no capacity is silently truncated.'
    }
    # Stay below exFAT's cluster-count limit for fixed 512 KiB clusters.
    if([uint64]$Request.disk.size -gt 128TB) {throw 'This 512 KiB formatter supports disks up to 128 TiB only.'}
}
function Get-VerifiedDataPartition($Disk,$Created,[string]$Style) {
    $parts=@(Get-Partition -ErrorAction Stop | Where-Object {$_.DiskNumber -eq $Disk.Number})
    $data=@($parts | Where-Object {$_.PartitionNumber -eq $Created.PartitionNumber})
    if([string]$Disk.PartitionStyle -ne $Style -or $Created.DiskNumber -ne $Disk.Number -or $data.Count -ne 1) {
        throw 'Created partition does not match the selected disk or partition style.'
    }
    $p=$data[0]
    if($p.DiskNumber -ne $Disk.Number -or $p.Offset -ne $Created.Offset -or $p.Size -ne $Created.Size -or
       [uint64]$p.Offset -lt 1MB -or ([uint64]$p.Offset % 1MB) -ne 0 -or
       ([uint64]$p.Size % [uint64]$Disk.LogicalSectorSize) -ne 0 -or
       [uint64]$p.Size -lt 512MB -or [uint64]$p.Offset -ge [uint64]$Disk.Size -or
       [uint64]$p.Size -gt ([uint64]$Disk.Size-[uint64]$p.Offset) -or
       ([uint64]$Disk.Size-[uint64]$p.Offset-[uint64]$p.Size) -gt 2MB) {
        throw 'Created data partition failed alignment, identity or full-capacity verification.'
    }
    $other=@($parts | Where-Object {$_.PartitionNumber -ne $p.PartitionNumber})
    if($Style -eq 'MBR') {
        if($other.Count -ne 0 -or $p.Offset -ne 1MB -or [int]$p.MbrType -ne 7) {throw 'Unexpected MBR partition layout.'}
    } else {
        if(([string]$p.GptType).Trim('{}') -ine 'ebd0a0a2-b9e5-4433-87c0-68b6b72699c7' -or $other.Count -gt 1) {throw 'Unexpected GPT data partition layout.'}
        # Windows may create a Microsoft Reserved partition during initialization.
        # Never format it, or silently accept a second data/EFI/recovery partition.
        foreach($reserved in $other) {
            if(([string]$reserved.GptType).Trim('{}') -ine 'e3c9e316-0b5c-4db8-817d-f92df00215ae' -or
               $reserved.Size -le 0 -or $reserved.Size -gt 128MB -or
               ([uint64]$reserved.Offset+[uint64]$reserved.Size) -gt [uint64]$p.Offset) {throw 'Unexpected GPT reserved partition.'}
        }
        $first=if($other.Count) {[uint64]$other[0].Offset+[uint64]$other[0].Size} else {[uint64]1MB}
        if($first -lt 1MB -or ([uint64]$p.Offset-$first) -ge 1MB -or [uint64]$p.Offset -gt 130MB) {throw 'Unexpected gap before GPT data partition.'}
    }
    return $p
}
function Get-NexusVerifiedVolume($Data,[string]$Label) {
    $volumes=@($Data | Get-Volume -ErrorAction Stop)
    if($volumes.Count -ne 1) {throw 'Final verification failed: expected one volume.'}
    $v=$volumes[0]
    # Allow bounded exFAT metadata, never silent filesystem truncation.
    $maxOverhead=[decimal]32MB + [Math]::Ceiling([double]$Data.Size / 524288) * 9
    if([string]$v.FileSystem -ne 'exFAT' -or [uint32]$v.AllocationUnitSize -ne 524288 -or
       [string]$v.FileSystemLabel -cne $Label -or [string]$v.HealthStatus -ne 'Healthy' -or
       [uint64]$v.Size -eq 0 -or [uint64]$v.Size -gt [uint64]$Data.Size -or
       ([decimal]$Data.Size-[decimal]$v.Size) -gt $maxOverhead) {
        throw 'Formatting returned, but the final disk/volume settings did not verify. Do not use the drive yet.'
    }
    return $v
}
function Invoke-NexusFormat($Request) {
    Assert-Request $Request
    $style=Get-Profile ([string]$Request.profile)
    $expected=$Request.disk
    $protect=@([string]$Request.appPath,[string]$Request.logDirectory)
    $disk=Get-ConfirmedDisk $expected $protect
    $fresh=Get-DiskSnapshot $disk (Get-ProtectedDisks $protect)
    if(-not $fresh.allowed) {throw ($fresh.reasons -join ' ')}
    $sameLayout=$fresh.style -ceq $expected.style -and @($fresh.partitions).Count -eq @($expected.partitions).Count
    if($sameLayout) {
        for($i=0;$i -lt @($fresh.partitions).Count;$i++) {
            foreach($field in @('number','offset','size','letter','label','filesystem')) {
                if([string]$fresh.partitions[$i].$field -cne [string]$expected.partitions[$i].$field) {$sameLayout=$false}
            }
        }
    }
    if(-not $sameLayout) {
        throw 'The partition layout changed after selection. Refresh and select the drive again.'
    }
    Set-NexusStage ('Verified disk identity, protections and profile: '+$style+' / exFAT / 512 KiB.')
    Send-Event 'diagnostic' ('Selected disk '+$disk.Number+': '+$disk.FriendlyName+'; capacity '+$disk.Size+
        ' bytes; logical sector '+$disk.LogicalSectorSize+' bytes; initial style '+$disk.PartitionStyle+
        '; partitions '+@($fresh.partitions).Count+'.')
    if(Test-LegacyLoaderLimit $expected) {Send-Event 'warning' 'This capacity needs Nexus USB 64-bit large-drive support (L1 or a later build retaining it). Older loaders may mount read-only or fail. Formatting does not update the Switch loader.'}
    Set-NexusStage 'Checking administrator disk-management privilege before erasing.'
    Enable-NexusDiskAccess
    Set-NexusStage 'Acquiring the native read/write session and exclusive volume locks before any erase.'
    $disk=Get-ConfirmedDisk $expected $protect
    Invoke-NexusOwnedFormat $disk $fresh $style ([string]$Request.label)
    Set-NexusStage 'Native metadata verified. Waiting for Windows read-only mount verification; no reformat retries.'
    $lastError='Windows has not exposed the new volume yet.'
    $v=$null
    for($attempt=0;$attempt -lt 20;$attempt++) {
        # Disk replacement/protection failures are not retried as mount delays.
        $disk=Get-ConfirmedDisk $expected $protect
        try {
            $parts=@(Get-Partition -ErrorAction Stop | Where-Object {$_.DiskNumber -eq $disk.Number})
            $data=@($parts | Where-Object {([string]$_.GptType).Trim('{}') -ieq 'ebd0a0a2-b9e5-4433-87c0-68b6b72699c7' -or [int]$_.MbrType -eq 7})
            if($data.Count -ne 1) {throw 'Expected exactly one native-created data partition.'}
            $data=Get-VerifiedDataPartition $disk $data[0] $style
            $v=Get-NexusVerifiedVolume $data ([string]$Request.label)
            break
        } catch {$lastError=$_.Exception.Message}
        Start-Sleep -Milliseconds 250
    }
    if($null -eq $v) {throw ('Raw metadata verified, but Windows mount verification is incomplete. Safely reconnect and inspect; do not reformat just for this. '+$lastError)}
    $mounted=$v.DriveLetter -and [int][char]$v.DriveLetter -ge 65
    $limited=Test-LegacyLoaderLimit $expected
    $message='Nexus native format and Windows mount verified: '+$style+' + exFAT + 512 KiB.'
    if(-not $mounted) {$message+=' Explorer drive letter is unavailable; reconnect or assign a letter, do not reformat.'}
    if($limited) {$message+=' A 64-bit large-drive loader is required on the Switch; formatter success does not verify that loader.'}
    Send-Event 'complete' $message @{
        letter=[string]$v.DriveLetter;size=[uint64]$v.Size;label=[string]$v.FileSystemLabel;style=$style;loaderLimit=$limited;mounted=$mounted
    }
}

function Invoke-NexusOwnedFormat($Disk,$Snapshot,[string]$Style,[string]$Label) {
    Import-NexusNative
    $progress=[Action[string]]{
        param($message)
        Set-NexusStage $message
        if($message -like 'Writing partition*') {Send-Event 'destructive' $message}
    }
    $result=[NexusFormatter.NativeStorage]::FormatOwned([string]$Disk.Path,[uint32]$Disk.Number,
        [uint64]$Disk.Size,[uint32]$Disk.LogicalSectorSize,([string]$Disk.SerialNumber).Trim(),[string]$Snapshot.style,
        [uint32[]]@($Snapshot.partitions | ForEach-Object {$_.number}),
        [uint64[]]@($Snapshot.partitions | ForEach-Object {$_.offset}),
        [uint64[]]@($Snapshot.partitions | ForEach-Object {$_.size}),$Style,$Label,$progress)
    Send-Event 'diagnostic' $result
}
