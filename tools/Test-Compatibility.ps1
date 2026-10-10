param([Parameter(Mandatory=$true)][string]$GameDirectory)
$ErrorActionPreference='Stop'
$checks=@(
    @{Name='GameAssembly.dll';Hash='8E50947F6E0B47FAA652E75A8DC65B10B209DC14FFEE0D8FB21B220C0CAB36A2'},
    @{Name='ShirenTheWanderer6_Data\il2cpp_data\Metadata\global-metadata.dat';Hash='7EE9883EFF203C1CCC030629D417356E5732C921AA5D8A4F10AB2DA80C29AFEF'}
)
$compatible=$true
foreach($check in $checks){
    $path=Join-Path $GameDirectory $check.Name
    if(!(Test-Path -LiteralPath $path -PathType Leaf)){Write-Output ('MISSING: '+$check.Name);$compatible=$false;continue}
    $actual=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    $ok=$actual -eq $check.Hash
    Write-Output ((@{True='MATCH';False='UNSUPPORTED'}[[string]$ok])+': '+$check.Name+' SHA256='+$actual)
    if(!$ok){$compatible=$false}
}
if(!$compatible){Write-Output 'This preview does not support these files. No files were changed.';exit 1}
Write-Output 'Files match the preview compatibility gate. No files were changed. Other mods and runtime compatibility are not checked.'
exit 0
