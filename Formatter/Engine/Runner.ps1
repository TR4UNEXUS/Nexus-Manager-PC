param([ValidateSet('Inventory','Format','InspectPartition','Convert','Recover')][string]$Mode='Inventory',[Parameter(Mandatory=$true)][string]$RequestPath)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
. (Join-Path $PSScriptRoot 'Format-Core.ps1')
. (Join-Path $PSScriptRoot 'Partition-Core.ps1')
. (Join-Path $PSScriptRoot 'Conversion-Core.ps1')
$owned=$false
$mutex=$null
try {
    $request=Get-Content -LiteralPath $RequestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if($Mode -eq 'Inventory') {
        $protected=Get-ProtectedDisks @([string]$request.appPath,[string]$request.logDirectory)
        $list=@()
        foreach($disk in @(Get-Disk -ErrorAction Stop | Where-Object { [string]$_.BusType -eq 'USB' })) {
            $list+=Get-DiskSnapshot $disk $protected
        }
        Send-Event 'inventory' ('Found '+$list.Count+' USB disk(s).') @($list)
    } else {
        $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
        if(-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Administrator access is required. Restart the app as administrator.'
        }
        $mutex=New-Object Threading.Mutex($false,'Global\NexusUsbFormatterDiskOperations')
        try {$owned=$mutex.WaitOne(0)} catch [Threading.AbandonedMutexException] {$owned=$true}
        if(-not $owned) {throw 'Another Nexus disk operation is active.'}
        switch($Mode) {
            'Format' {Invoke-NexusFormat $request}
            'InspectPartition' { $report=Get-NexusExfatReport $request; Send-Event 'report' $report.summary $report }
            'Convert' {Invoke-NexusConversion $request}
            'Recover' {Invoke-NexusConversion $request $true}
        }
    }
} catch {
    $detail=Get-NexusErrorDetail $_
    $hint=Get-NexusRecoveryHint $_
    Send-Event 'error' ('Failed during: '+$script:NexusStage+[Environment]::NewLine+$hint+[Environment]::NewLine+$detail) @{hint=$hint}
    exit 1
} finally {
    if($owned) {$mutex.ReleaseMutex()}
    if($null -ne $mutex) {$mutex.Dispose()}
}
