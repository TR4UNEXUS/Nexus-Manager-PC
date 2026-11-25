param([Parameter(Mandatory)][string]$OutputDirectory,[string]$CompressionDotnet='dotnet',[string]$InstallerCompiler)
$ErrorActionPreference='Stop'
dotnet publish (Join-Path $PSScriptRoot 'NexusManagerPC.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o ([IO.Path]::GetFullPath($OutputDirectory))
if($LASTEXITCODE){throw 'Windows build failed.'}
$compressionDir=Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'Compression'
& $CompressionDotnet publish (Join-Path $PSScriptRoot '../CompressionHost/CompressionHost.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $compressionDir
if($LASTEXITCODE){throw 'Compression engine build failed. Use -CompressionDotnet to select a .NET 10 SDK.'}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../CompressionHost/upstream/LICENSE') -Destination (Join-Path $compressionDir 'LICENSE')
$sourceDir=(Resolve-Path (Join-Path $PSScriptRoot '../CompressionHost')).Path
$sourceFiles=@('CompressionHost.csproj','NexusCompressionHost.cs','ORIGIN.txt','upstream') | ForEach-Object {Join-Path $sourceDir $_}
Compress-Archive -LiteralPath $sourceFiles -DestinationPath (Join-Path $compressionDir 'CompressionHost-source.zip') -Force
if($InstallerCompiler){
    $payloadPath=[IO.Path]::GetFullPath($OutputDirectory)
    & $InstallerCompiler "/DPayloadDir=$payloadPath" "/DDestination=$payloadPath" (Join-Path $PSScriptRoot 'Packaging/Desktop.iss')
    if($LASTEXITCODE){throw 'Windows installer build failed.'}
}
