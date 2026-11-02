[CmdletBinding()]
param([string]$Distribution = 'Ubuntu')
. (Join-Path $PSScriptRoot 'common.ps1')
$windowsRoot = $script:RepositoryRoot.Replace('\', '/')
$wslPath = & wsl.exe -d $Distribution --exec wslpath -a $windowsRoot
if ($LASTEXITCODE -ne 0) { throw 'An Ubuntu WSL distribution is required for native local RabbitMQ setup.' }
$wslRoot = ($wslPath | Select-Object -Last 1).Trim()
Invoke-CheckedCommand wsl.exe @('-d', $Distribution, '-u', 'root', '--', 'bash',
    "$wslRoot/scripts/deploy/install-local-rabbitmq-wsl.sh", "$wslRoot/.env.messaging.local")
$envPath = Join-Path $script:RepositoryRoot '.env.messaging.local'
$owner = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$grant = '{0}:(F)' -f $owner
& icacls.exe $envPath /inheritance:r /grant:r $grant | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not restrict .env.messaging.local to the current Windows user.' }
Write-Host 'RabbitMQ ready: AMQP 127.0.0.1:5672; management http://127.0.0.1:15672. Credentials: .env.messaging.local'
