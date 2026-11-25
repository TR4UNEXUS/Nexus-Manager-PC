# Guarded conversion/recovery dispatcher. Loading defines functions only.
# Never invokes FormatOwned, a Windows formatter or an erase fallback.
function Assert-NexusConversionRequest($Request,[bool]$Recovery) {
    $action=if($Recovery){'recover-conversion'}else{'convert-keep-files'}
    if($Request.schema -ne 5 -or $Request.action -cne $action -or
       $Request.userInitiated -isnot [bool] -or -not $Request.userInitiated -or
       $Request.backupConfirmed -isnot [bool] -or -not $Request.backupConfirmed) {throw 'An explicit confirmed conversion/recovery request is required.'}
    $age=([DateTime]::UtcNow-[DateTime]::Parse([string]$Request.issuedUtc).ToUniversalTime()).TotalSeconds
    if($age -lt -5 -or $age -gt 120){throw 'Request expired. Select the operation again.'}
    if(-not(Test-Path -LiteralPath ([string]$Request.appPath) -PathType Leaf)){throw 'Application location is unavailable.'}
    $journal=[IO.Path]::GetFullPath([string]$Request.journalPath)
    if($journal -cne [string]$Request.journalPath -or $journal.StartsWith('\\') -or
       [IO.Path]::GetExtension($journal) -cne '.nxcv' -or
       -not(Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($journal)) -PathType Container)){throw 'Choose a recovery file on a local PC drive.'}
    if($Recovery) {
        if(-not(Test-Path -LiteralPath $journal -PathType Leaf)){throw 'Recovery journal is missing.'}
    } else {
        if(Test-Path -LiteralPath $journal){throw 'Recovery file already exists; never overwrite it.'}
        if($Request.targetStyle -cnotin @('MBR','GPT') -or $Request.convertClusters -isnot [bool]){throw 'Unsupported conversion options.'}
    }
}
function Invoke-NexusNativeConversion($Disk,$Snapshot,$Partition,$Request,[bool]$Recovery) {
    Import-NexusNative
    $numbers=[uint32[]]@($Snapshot.partitions | ForEach-Object {$_.number})
    $offsets=[uint64[]]@($Snapshot.partitions | ForEach-Object {$_.offset})
    $sizes=[uint64[]]@($Snapshot.partitions | ForEach-Object {$_.size})
    $progress=[Action[string]]{param($message) Set-NexusStage $message}
    $started=[Action]{Send-Event 'changed' 'Conversion writes started. Keep drive connected; PC journal retained.'}
    if($Recovery) {
        return [NexusFormatter.NativeStorage]::RecoverConversion([string]$Disk.Path,[uint32]$Disk.Number,[uint64]$Disk.Size,[uint32]$Disk.LogicalSectorSize,
            ([string]$Disk.SerialNumber).Trim(),[string]$Disk.PartitionStyle,$numbers,$offsets,$sizes,[string]$Request.journalPath,$progress,$started)
    }
    return [NexusFormatter.NativeStorage]::ConvertOwned([string]$Disk.Path,[uint32]$Disk.Number,[uint64]$Disk.Size,[uint32]$Disk.LogicalSectorSize,
        ([string]$Disk.SerialNumber).Trim(),[string]$Disk.PartitionStyle,$numbers,$offsets,$sizes,[uint64]$Partition.Offset,[uint64]$Partition.Size,
        [string]$Request.targetStyle,[bool]$Request.convertClusters,[string]$Request.journalPath,$progress,$started)
}
function Invoke-NexusConversion($Request,[bool]$Recovery=$false) {
    Assert-NexusConversionRequest $Request $Recovery
    $protect=@([string]$Request.appPath,[string]$Request.logDirectory,[string]$Request.journalPath)
    $disk=Get-NexusInspectionDisk $Request.disk
    if([uint64]$disk.Size -gt 128TB){throw 'This conversion engine supports disks up to 128 TiB only.'}
    $snapshot=Get-DiskSnapshot $disk (Get-ProtectedDisks $protect)
    $reasons=@($snapshot.reasons)
    if($Recovery) {
        if(-not $disk.IsOffline){throw 'Recovery only accepts an interrupted drive that is still offline. It will not overwrite a drive currently in use.'}
        $reasons=@($reasons | Where-Object {$_ -cne 'Disk is offline.'})
    } else {Assert-NexusInspectionLayout $Request.disk $snapshot}
    if($reasons.Count){throw ($reasons -join ' ')}
    $partition=$null
    if(-not $Recovery) {
        $parts=@(Get-Partition -ErrorAction Stop | Where-Object {$_.DiskNumber -eq $disk.Number})
        $selected=@($parts | Where-Object {$_.PartitionNumber -eq $Request.partitionNumber})
        if($selected.Count -ne 1){throw 'Select one existing exFAT partition.'}
        $partition=$selected[0]
        if($partition.IsBoot -or $partition.IsSystem -or $partition.IsReadOnly -or $partition.IsOffline -or
           $partition.IsHidden -or $partition.IsShadowCopy -or $partition.IsActive -or
           -not(Test-NexusInspectionState $partition.OperationalStatus 'Online') -or
           -not(Test-NexusInspectionState $partition.TransitionState 'Stable')) {throw 'Partition must be online, stable and unprotected.'}
        $volumes=@($partition | Get-Volume -ErrorAction Stop)
        if($volumes.Count -ne 1 -or $volumes[0].FileSystem -ine 'exFAT' -or $volumes[0].HealthStatus -cne 'Healthy'){throw 'Conversion requires one healthy exFAT volume; no format fallback.'}
    }
    Enable-NexusDiskAccess
    # Repeat identity/protection checks immediately before opening the native lease.
    $freshDisk=Get-NexusInspectionDisk $Request.disk
    $fresh=Get-DiskSnapshot $freshDisk (Get-ProtectedDisks $protect)
    Assert-NexusInspectionLayout $snapshot $fresh
    $freshReasons=@($fresh.reasons | Where-Object {-not($Recovery -and $_ -ceq 'Disk is offline.')})
    if($freshReasons.Count){throw ($freshReasons -join ' ')}
    $message=if($Recovery){'Opening interrupted conversion for verified recovery.'}else{'Preparing file-preserving conversion. No format fallback.'}
    Set-NexusStage $message
    $result=Invoke-NexusNativeConversion $freshDisk $fresh $partition $Request $Recovery
    Send-Event 'complete' $result @{journal=[string]$Request.journalPath;recovery=$Recovery}
}
