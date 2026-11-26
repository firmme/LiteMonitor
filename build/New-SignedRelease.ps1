#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string] $Version,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string] $CertificateThumbprint,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/releases')
)

$ErrorActionPreference = 'Stop'
if (([version]$Version).ToString() -cne $Version) { throw 'Use a canonical version such as 1.3.9.' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$certificate = Get-Item "Cert:\CurrentUser\My\$CertificateThumbprint"
$key = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
if ($null -eq $key) { throw 'The signing certificate has no accessible RSA private key.' }

try {
    $publicKey = (Get-Content -Raw (Join-Path $repo 'resources/update-public-key.pem')).Replace([string][char]13, '').Trim()
    if ($key.KeySize -lt 2048 -or $key.ExportSubjectPublicKeyInfoPem().Replace([string][char]13, '').Trim() -cne $publicKey) {
        throw 'The certificate does not match the update public key in the repository.'
    }

    $output = [IO.Path]::GetFullPath($OutputDirectory)
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $name = "LiteMonitor_v$Version-win-x64.zip"
    $destination = Join-Path $output $name
    if ((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath "$destination.sig")) {
        throw "Release assets already exist at $destination."
    }

    $stage = Join-Path $output ('.release-' + [guid]::NewGuid().ToString('N'))
    $publish = Join-Path $stage 'publish'
    [IO.Directory]::CreateDirectory($publish) | Out-Null
    try {
        $publishArgs = @(
            'publish', (Join-Path $repo 'LiteMonitor.csproj'), '-c', 'Release', '-r', 'win-x64',
            '--self-contained', 'true', '-p:PublishSingleFile=true', "-p:Version=$Version",
            "-p:FileVersion=$Version.0", '-o', $publish
        )
        & dotnet @publishArgs | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }
        if (-not (Test-Path -LiteralPath (Join-Path $publish 'resources/LiteMonitor.Updater.exe'))) {
            throw 'The published package does not include the bundled updater.'
        }

        $zip = Join-Path $stage $name
        [IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
        $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
        $bytes = [Text.Encoding]::UTF8.GetBytes([string]::Join([char]10, @('LiteMonitor', $Version, $hash, '')))
        $signature = $key.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss)
        [IO.File]::WriteAllBytes("$zip.sig", $signature)
        [IO.File]::Move("$zip.sig", "$destination.sig")
        try { [IO.File]::Move($zip, $destination) }
        catch { [IO.File]::Delete("$destination.sig"); throw }
        Get-Item -LiteralPath $destination, "$destination.sig"
    }
    finally {
        $resolvedStage = [IO.Path]::GetFullPath($stage)
        if ([IO.Path]::GetDirectoryName($resolvedStage) -eq $output -and
            [IO.Path]::GetFileName($resolvedStage) -match '^\.release-[a-f0-9]{32}$' -and
            (Test-Path -LiteralPath $resolvedStage) -and
            -not ((Get-Item -LiteralPath $resolvedStage -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            Remove-Item -LiteralPath $resolvedStage -Recurse -Force
        }
    }
}
finally {
    $key.Dispose()
}
