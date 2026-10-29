# Independent reference readings from stock Windows tools (CIM cmdlets, Get-Counter, Get-Process).
# Used only by HardwareLens self-test to cross-check the app's own collectors. Read-only. Writes JSON.
param([Parameter(Mandatory)][string]$Out, [int]$Seconds = 12)
$ErrorActionPreference = 'Continue'
$r = [ordered]@{}
function T($b){ try { & $b } catch { $null } }
$r.elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$r.cpu = @(T { Get-CimInstance Win32_Processor | ForEach-Object { [ordered]@{ name=$_.Name.Trim(); cores=$_.NumberOfCores; threads=$_.NumberOfLogicalProcessors; maxMHz=$_.MaxClockSpeed } } })
$r.board = T { $b = Get-CimInstance Win32_BaseBoard; [ordered]@{ mfr=$b.Manufacturer; model=$b.Product } }
$r.bios = T { $b = Get-CimInstance Win32_BIOS; [ordered]@{ version=$b.SMBIOSBIOSVersion; date=$b.ReleaseDate.ToString('yyyy-MM-dd') } }
$r.ramModules = @(T { Get-CimInstance Win32_PhysicalMemory | ForEach-Object { [ordered]@{ slot=$_.DeviceLocator; mfr=$_.Manufacturer; part=($_.PartNumber + '').Trim(); capacity=[uint64]$_.Capacity; configured=$_.ConfiguredClockSpeed; speed=$_.Speed } } })
$r.ramTotal = T { [uint64](Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory }
$r.os = T { $o = Get-CimInstance Win32_OperatingSystem; $k = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'; [ordered]@{ caption=$o.Caption; build=$o.BuildNumber; ubr=$k.UBR; display=$k.DisplayVersion; lastBoot=$o.LastBootUpTime.ToString('o') } }
$r.gpus = @(T { Get-CimInstance Win32_VideoController | ForEach-Object { [ordered]@{ name=$_.Name; driver=$_.DriverVersion } } })
$r.gpuRegistry = @(T { Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}' -ErrorAction SilentlyContinue | ForEach-Object { $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue; if ($p.DriverDesc -and $p.'HardwareInformation.qwMemorySize') { [ordered]@{ name=$p.DriverDesc; vram=[uint64]$p.'HardwareInformation.qwMemorySize' } } } })
$r.disks = @(T { Get-PhysicalDisk | ForEach-Object { [ordered]@{ number=[int]$_.DeviceId; name=$_.FriendlyName; media="$($_.MediaType)"; bus="$($_.BusType)"; size=[uint64]$_.Size; health="$($_.HealthStatus)" } } })
$r.volumes = @(T { Get-Volume | Where-Object DriveLetter | ForEach-Object { [ordered]@{ letter="$($_.DriveLetter):"; fs=$_.FileSystem; size=[uint64]$_.Size; free=[uint64]$_.SizeRemaining } } })
$r.nics = @(T { Get-NetAdapter | ForEach-Object { [ordered]@{ name=$_.Name; desc=$_.InterfaceDescription; status="$($_.Status)"; linkSpeed=$_.LinkSpeed; hw=$_.HardwareInterface; virtual=$_.Virtual } } })
$r.battery = @(T { Get-CimInstance Win32_Battery | ForEach-Object { [ordered]@{ name=$_.Name; charge=$_.EstimatedChargeRemaining } } })

# Process snapshot #1
$n = [Environment]::ProcessorCount
$p1 = @{}; $t1 = Get-Date
Get-Process | ForEach-Object { $p1[$_.Id] = $_.CPU }
# Live counters (1 s interval) while HardwareLens samples at the same time
$paths = '\Processor Information(_Total)\% Processor Time','\Memory\Available MBytes','\PhysicalDisk(_Total)\Disk Read Bytes/sec','\PhysicalDisk(_Total)\Disk Write Bytes/sec','\PhysicalDisk(_Total)\% Idle Time','\Network Interface(*)\Bytes Received/sec','\Network Interface(*)\Bytes Sent/sec','\GPU Engine(*)\Utilization Percentage','\GPU Adapter Memory(*)\Dedicated Usage','\GPU Adapter Memory(*)\Shared Usage'
$samples = @()
try {
  $c = Get-Counter -Counter $paths -SampleInterval 1 -MaxSamples $Seconds -ErrorAction SilentlyContinue
  foreach ($s in $c) {
    $row = [ordered]@{ t=$s.Timestamp.ToString('o'); cpu=$null; availMB=$null; diskR=$null; diskW=$null; diskIdle=$null; netRx=0.0; netTx=0.0; gpuSum=0.0; gpuMax=0.0; gpuDed=0.0; gpuShared=0.0 }
    $gpuByEng = @{}
    foreach ($x in $s.CounterSamples) {
      $pth = $x.Path
      if ($pth -like '*processor information(_total)*') { $row.cpu = $x.CookedValue }
      elseif ($pth -like '*available mbytes*') { $row.availMB = $x.CookedValue }
      elseif ($pth -like '*physicaldisk(_total)*read bytes*') { $row.diskR = $x.CookedValue }
      elseif ($pth -like '*physicaldisk(_total)*write bytes*') { $row.diskW = $x.CookedValue }
      elseif ($pth -like '*physicaldisk(_total)*idle*') { $row.diskIdle = $x.CookedValue }
      elseif ($pth -like '*network interface*received*') { $row.netRx += $x.CookedValue }
      elseif ($pth -like '*network interface*sent*') { $row.netTx += $x.CookedValue }
      elseif ($pth -like '*gpu engine*') { $row.gpuSum += $x.CookedValue; $m = [regex]::Match($x.InstanceName, 'luid_(0x[0-9a-f]+_0x[0-9a-f]+)_phys_(\d+)_eng_(\d+)_engtype_(.+)$'); if ($m.Success) { $k = $m.Groups[1].Value + '|' + $m.Groups[3].Value + '|' + $m.Groups[4].Value; $gpuByEng[$k] += $x.CookedValue } }
      elseif ($pth -like '*gpu adapter memory*dedicated*') { $row.gpuDed += $x.CookedValue }
      elseif ($pth -like '*gpu adapter memory*shared*') { $row.gpuShared += $x.CookedValue }
    }
    if ($gpuByEng.Count -gt 0) { $row.gpuMax = ($gpuByEng.Values | Measure-Object -Maximum).Maximum }
    $samples += $row
  }
} catch { $r.counterError = $_.Exception.Message }
$r.counters = $samples
# Process snapshot #2
$t2 = Get-Date; $dt = ($t2 - $t1).TotalSeconds
$procs = @()
Get-Process | ForEach-Object {
  $prev = $p1[$_.Id]
  $cpu = $null; if ($null -ne $prev -and $null -ne $_.CPU -and $dt -gt 0) { $cpu = [math]::Round((($_.CPU - $prev) / $dt / $n) * 100, 2) }
  $procs += [ordered]@{ pid=$_.Id; name=$_.ProcessName; cpuPct=$cpu; workingSet=[uint64]$_.WorkingSet64; privateBytes=[uint64]$_.PrivateMemorySize64 }
}
$r.processWindowSeconds = $dt; $r.logicalProcessors = $n
$r.processes = $procs
$r | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Out -Encoding UTF8
