# Read-only exFAT profile inspection. No resize, conversion or repair path.
# Defining these functions does not enumerate or open storage devices.
function Assert-NexusInspectionRequest($Request) {
    if($Request.schema -ne 4 -or [string]$Request.action -cne 'inspect-partition' -or
       $Request.userInitiated -isnot [bool] -or -not $Request.userInitiated) {throw 'An explicit read-only inspection request is required.'}
    $age=([DateTime]::UtcNow-[DateTime]::Parse([string]$Request.issuedUtc).ToUniversalTime()).TotalSeconds
    if($age -lt -5 -or $age -gt 120) {throw 'Inspection request expired. Refresh and select the partition again.'}
    if(-not (Test-Path -LiteralPath ([string]$Request.appPath) -PathType Leaf)) {throw 'Cannot verify the application location.'}
}
function Assert-NexusInspectionLayout($Expected,$Actual) {
    if($Expected.style -cne $Actual.style -or @($Expected.partitions).Count -ne @($Actual.partitions).Count) {throw 'Partition layout changed. Refresh and inspect again.'}
    foreach($old in @($Expected.partitions)) {
        $matches=@($Actual.partitions | Where-Object {$_.number -eq $old.number})
        if($matches.Count -ne 1) {throw 'Partition layout changed or is ambiguous.'}
        foreach($field in @('number','offset','size','letter','label','filesystem')) {
            if([string]$matches[0].$field -cne [string]$old.$field) {throw ('Partition layout changed: '+$field+'. Refresh and inspect again.')}
        }
    }
}
function Get-NexusInspectionDisk($Expected) {
    $matches=@(Get-Disk -ErrorAction Stop | Where-Object {[string]$_.Path -ceq [string]$Expected.path})
    if($matches.Count -ne 1) {throw 'The selected physical USB disk is missing or ambiguous.'}
    Assert-Identity $Expected $matches[0]
    return $matches[0]
}
function Get-NexusInspectionPartitionKey($Parts) {
    return ConvertTo-Json -InputObject @($Parts | Sort-Object PartitionNumber | Select-Object PartitionNumber,Offset,Size,Guid,GptType,MbrType,IsBoot,IsSystem,IsHidden,IsOffline,IsReadOnly,IsShadowCopy,IsActive,OperationalStatus,TransitionState) -Compress
}
function Test-NexusInspectionState($Value,[string]$ExpectedName) {
    # Storage.types.ps1xml projects MSFT_Partition.OperationalStatus as text
    # (1 => Online). CIM/raw mocks may instead expose the integer. Do not cast
    # display text, round floats, coerce booleans, or treat unknown as healthy.
    if($Value -is [string]) {
        $text=$Value.Trim()
        return $text -ieq $ExpectedName -or $text -ceq '1'
    }
    if($Value -is [byte] -or $Value -is [sbyte] -or $Value -is [int16] -or
       $Value -is [uint16] -or $Value -is [int32] -or $Value -is [uint32] -or
       $Value -is [int64] -or $Value -is [uint64] -or $Value -is [Enum]) {
        return [decimal]$Value -eq 1
    }
    return $false
}
function Get-NexusExfatReport($Request) {
    Assert-NexusInspectionRequest $Request
    Set-NexusStage 'Reading existing partition and exFAT settings. No resize, repair or format.'
    $protect=@([string]$Request.appPath,[string]$Request.logDirectory)
    $disk=Get-NexusInspectionDisk $Request.disk
    $snapshot=Get-DiskSnapshot $disk (Get-ProtectedDisks $protect)
    Assert-NexusInspectionLayout $Request.disk $snapshot
    $parts=@(Get-Partition -ErrorAction Stop | Where-Object {$_.DiskNumber -eq $disk.Number})
    if($parts.Count -ne @($snapshot.partitions).Count) {throw 'Layout changed during inspection.'}
    foreach($part in $parts) {
        $old=@($snapshot.partitions | Where-Object {$_.number -eq $part.PartitionNumber})
        if($old.Count -ne 1 -or $old[0].offset -ne $part.Offset -or $old[0].size -ne $part.Size) {throw 'Layout changed during inspection.'}
    }
    $selected=@($parts | Where-Object {$_.PartitionNumber -eq [int]$Request.partitionNumber})
    if($selected.Count -ne 1) {throw 'Select exactly one existing partition.'}
    $partitionKey=Get-NexusInspectionPartitionKey $parts
    $p=$selected[0]
    $volumes=@($p | Get-Volume -ErrorAction Stop)
    if($volumes.Count -ne 1) {throw 'Windows did not expose exactly one readable volume. No repair or format was attempted.'}
    $v=$volumes[0]
    # Store scalars, not live CIM objects, for the final read consistency check.
    $volumeKey=@([string]$v.UniqueId,[string]$v.Path,[string]$v.FileSystem,[string]$v.AllocationUnitSize,[string]$v.Size,[string]$v.HealthStatus) -join '|'
    $issues=New-Object 'System.Collections.Generic.List[string]'
    $notes=New-Object 'System.Collections.Generic.List[string]'
    $notes.Add(('Partition #{0}: {1}; {2} table; logical sectors {3} bytes.' -f $p.PartitionNumber,$v.FileSystem,$disk.PartitionStyle,$disk.LogicalSectorSize))
    $notes.Add(('Partition: {0:N0} bytes. Filesystem: {1:N0} bytes. Free: {2:N0} bytes.' -f [decimal]$p.Size,[decimal]$v.Size,[decimal]$v.SizeRemaining))
    $notes.Add(('Allocation unit: {0:N0} bytes. Nexus format profile: 524,288 bytes (512 KiB).' -f [decimal]$v.AllocationUnitSize))
    $notes.Add(('Windows volume health: {0}. This is not a surface or file-integrity test.' -f $v.HealthStatus))
    foreach($reason in @($snapshot.reasons)) {$issues.Add([string]$reason)}
    if([string]$v.FileSystem -ine 'exFAT') {$issues.Add('Not exFAT. This USB Loader workflow does not offer NTFS. No filesystem conversion was attempted.')}
    if([uint32]$v.AllocationUnitSize -ne 524288) {$issues.Add('Cluster size differs from the Nexus format profile. This alone does NOT establish why a game fails. Do not erase a working drive just to change it.')}
    if([string]$v.HealthStatus -cne 'Healthy') {$issues.Add('Windows does not report this volume Healthy. Back up before considering any repair.')}
    if([string]::IsNullOrWhiteSpace([string]$v.UniqueId) -or [string]::IsNullOrWhiteSpace([string]$v.Path)) {$issues.Add('Volume identity could not be fully read.')}
    if([decimal]$v.Size -le 0 -or [decimal]$v.Size -gt [decimal]$p.Size -or [decimal]$v.SizeRemaining -lt 0 -or [decimal]$v.SizeRemaining -gt [decimal]$v.Size) {$issues.Add('Filesystem capacity/free-space values are inconsistent.')}
    if([string]$v.FileSystem -ieq 'exFAT' -and [uint32]$v.AllocationUnitSize -eq 524288) {
        $overhead=[decimal]32MB+[Math]::Ceiling([double]$p.Size/524288)*9
        if(([decimal]$p.Size-[decimal]$v.Size) -gt $overhead) {$issues.Add('Filesystem appears substantially smaller than its partition. No automatic expansion or repair was attempted.')}
    }
    if([int]$disk.LogicalSectorSize -notin @(512,4096)) {$issues.Add('Logical sector size is outside the formatter profile.')}
    if([string]$disk.PartitionStyle -notin @('MBR','GPT')) {$issues.Add('Partition style is not basic MBR or GPT.')}
    if([string]$disk.PartitionStyle -eq 'MBR' -and [uint64]$disk.Size -gt 2000000000000) {$issues.Add('This disk exceeds the formatter 2 TB MBR policy. GPT is the profile for a fresh format; this is NOT a request to convert or erase an existing drive.')}
    if([decimal]$disk.Size -gt 128TB) {$issues.Add('Capacity exceeds this formatter profile (128 TiB).')}
    $end=[decimal]0;$allocated=[decimal]0
    foreach($part in @($parts | Sort-Object Offset)) {
        if([decimal]$part.Offset -lt $end -or [decimal]$part.Size -le 0 -or ([decimal]$part.Offset+[decimal]$part.Size) -gt [decimal]$disk.Size) {$issues.Add('Partition geometry overlaps or exceeds the physical disk. No changes attempted.')}
        if([int]$disk.LogicalSectorSize -gt 0 -and (([decimal]$part.Offset%[int]$disk.LogicalSectorSize) -ne 0 -or ([decimal]$part.Size%[int]$disk.LogicalSectorSize) -ne 0)) {$issues.Add('Partition geometry is not sector aligned.')}
        $end=[decimal]$part.Offset+[decimal]$part.Size;$allocated+=[decimal]$part.Size
    }
    if([decimal]$p.Offset -lt 1MB -or ([decimal]$p.Offset%1MB) -ne 0) {$issues.Add('Selected data partition must start at or beyond 1 MiB on a 1 MiB boundary to match this profile.')}
    $others=@($parts | Where-Object {$_.PartitionNumber -ne $p.PartitionNumber})
    if([string]$disk.PartitionStyle -eq 'GPT') {
        if(([string]$p.GptType).Trim('{}') -ine 'ebd0a0a2-b9e5-4433-87c0-68b6b72699c7') {$issues.Add('Selected GPT partition is not a basic data partition.')}
        if($others.Count -gt 1 -or @($others | Where-Object {([string]$_.GptType).Trim('{}') -ine 'e3c9e316-0b5c-4db8-817d-f92df00215ae' -or $_.Size -gt 128MB -or ([decimal]$_.Offset+[decimal]$_.Size) -gt [decimal]$p.Offset}).Count) {$issues.Add('Additional/nonstandard partitions differ from the single-game-partition profile. No partitions were removed or merged.')}
    } elseif($others.Count -gt 0 -or [int]$p.MbrType -ne 7) {$issues.Add('MBR layout differs from the single type-7 exFAT data-partition profile.')}
    if($p.IsBoot -or $p.IsSystem -or $p.IsHidden -or $p.IsOffline -or $p.IsReadOnly -or $p.IsShadowCopy -or $p.IsActive) {$issues.Add('Selected partition has protected/special attributes. Nothing was changed.')}
    $operational=$p.PSObject.Properties['OperationalStatus']
    $transition=$p.PSObject.Properties['TransitionState']
    $online=$null -ne $operational -and (Test-NexusInspectionState $operational.Value 'Online')
    $stable=$null -ne $transition -and (Test-NexusInspectionState $transition.Value 'Stable')
    $operationText=if($null -eq $operational -or $null -eq $operational.Value){'(unavailable)'}else{[string]$operational.Value}
    $transitionText=if($null -eq $transition -or $null -eq $transition.Value){'(unavailable)'}else{[string]$transition.Value}
    $notes.Add(('Partition status: {0}; transition: {1}.' -f $operationText,$transitionText))
    if(-not $online -or -not $stable) {$issues.Add('Partition is not reported online/stable, or its status is unavailable/unrecognized. Close other disk tools and refresh.')}
    $outside=[Math]::Max([decimal]0,[decimal]$disk.Size-$allocated)
    $notes.Add(('{0:N0} bytes outside partitions (includes table/alignment space).' -f $outside))
    if($outside -gt 256MB) {$issues.Add('Substantial space lies outside existing partitions. exFAT resizing is not implemented; it will not be silently formatted or expanded.')}
    if([int]$disk.LogicalSectorSize -gt 0 -and [decimal]$disk.Size/[int]$disk.LogicalSectorSize -gt [decimal]4294967296) {$notes.Add('Beyond 32-bit sector addressing: requires a loader with 64-bit USB addressing support. The installed loader cannot be checked from this PC.')}
    $notes.Add('Do not reformat solely because a game will not boot. This check does not validate USB indexes, game files or the USB bridge on the Switch.')
    $finalDisk=Get-NexusInspectionDisk $Request.disk
    $final=Get-DiskSnapshot $finalDisk (Get-ProtectedDisks $protect)
    Assert-NexusInspectionLayout $snapshot $final
    $finalParts=@(Get-Partition -ErrorAction Stop | Where-Object {$_.DiskNumber -eq $disk.Number})
    if((Get-NexusInspectionPartitionKey $finalParts) -cne $partitionKey) {throw 'Partition identity/attributes changed during inspection. Refresh and inspect again.'}
    $finalPart=@($finalParts | Where-Object {$_.PartitionNumber -eq $p.PartitionNumber})
    if($finalPart.Count -ne 1 -or $finalPart[0].Offset -ne $p.Offset -or $finalPart[0].Size -ne $p.Size) {throw 'Partition changed during inspection.'}
    $last=@($finalPart[0] | Get-Volume -ErrorAction Stop)
    if($last.Count -ne 1) {throw 'Volume disappeared during inspection.'}
    $key=@([string]$last[0].UniqueId,[string]$last[0].Path,[string]$last[0].FileSystem,[string]$last[0].AllocationUnitSize,[string]$last[0].Size,[string]$last[0].HealthStatus) -join '|'
    if($key -cne $volumeKey) {throw 'Volume identity/settings changed during inspection. Refresh and inspect again.'}
    foreach($reason in @($final.reasons)) {if(-not $issues.Contains([string]$reason)){$issues.Add([string]$reason)}}
    $status=if($issues.Count){'REVIEW FINDINGS'}else{'PROFILE SETTINGS MATCH'}
    $lines=@($status)+@($notes)+@($issues | ForEach-Object {'CHECK: '+$_})+@('READ-ONLY: no files or partitions changed. Not a guarantee that games boot. This inspection does not resize, convert or repair.')
    return [pscustomobject]@{status=$status;summary=($lines -join [Environment]::NewLine);notes=@($notes);issues=@($issues);partitionNumber=[int]$p.PartitionNumber;disk=$final}
}
