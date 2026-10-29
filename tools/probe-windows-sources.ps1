# Read-only probe of the Windows data sources HardwareLens relies on.
# Writes a text report; contains NO serial numbers or MAC addresses.
param([string]$Out = (Join-Path $PSScriptRoot '..\qa\private\probe.txt'))
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
$o = New-Object System.Collections.Generic.List[string]
function Sec($t){ $o.Add(''); $o.Add("=== $t ===") }
function Try-Do($label, [scriptblock]$b){ try { & $b | Out-String -Width 220 | ForEach-Object { $o.Add($_.TrimEnd()) } } catch { $o.Add("$label FAILED: $($_.Exception.Message)") } }
Sec 'Identity'; $o.Add("PS $($PSVersionTable.PSVersion) | Elevated: " + ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
Sec 'OS'; Try-Do OS { Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber,OSArchitecture,LastBootUpTime,TotalVisibleMemorySize,FreePhysicalMemory | Format-List }
Try-Do reg { Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' | Select-Object ProductName,DisplayVersion,CurrentBuild,UBR,EditionID | Format-List }
Sec 'CPU'; Try-Do CPU { Get-CimInstance Win32_Processor | Select-Object Name,Manufacturer,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed,CurrentClockSpeed,SocketDesignation,L2CacheSize,L3CacheSize | Format-List }
Sec 'Board/BIOS'; Try-Do board { Get-CimInstance Win32_BaseBoard | Select-Object Manufacturer,Product,Version | Format-List }
Try-Do bios { Get-CimInstance Win32_BIOS | Select-Object Manufacturer,SMBIOSBIOSVersion,Version,ReleaseDate | Format-List }
Try-Do cs { Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer,Model,SystemType,PCSystemType,TotalPhysicalMemory | Format-List }
Sec 'RAM modules'; Try-Do ram { Get-CimInstance Win32_PhysicalMemory | Select-Object BankLabel,DeviceLocator,Manufacturer,PartNumber,Capacity,Speed,ConfiguredClockSpeed,SMBIOSMemoryType,MemoryType,FormFactor,ConfiguredVoltage | Format-Table -AutoSize }
Try-Do arr { Get-CimInstance Win32_PhysicalMemoryArray | Select-Object MemoryDevices,MaxCapacity,MaxCapacityEx,Use | Format-List }
Sec 'GPU (CIM)'; Try-Do gpu { Get-CimInstance Win32_VideoController | Select-Object Name,AdapterCompatibility,AdapterRAM,DriverVersion,VideoProcessor,Status,PNPDeviceID | Format-List }
Sec 'GPU (registry qwMemorySize)'; Try-Do gpureg { Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}' -ErrorAction SilentlyContinue | ForEach-Object { $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue; if($p.DriverDesc){ [pscustomobject]@{Key=$_.PSChildName;DriverDesc=$p.DriverDesc;qwMemorySize=$p.'HardwareInformation.qwMemorySize'} } } | Format-Table -AutoSize }
Sec 'Physical disks (Storage)'; Try-Do pd { Get-PhysicalDisk | Select-Object DeviceId,FriendlyName,Model,Manufacturer,MediaType,BusType,Size,HealthStatus,OperationalStatus,SpindleSpeed | Format-Table -AutoSize }
Sec 'Win32_DiskDrive'; Try-Do dd { Get-CimInstance Win32_DiskDrive | Select-Object Index,Model,InterfaceType,MediaType,Size,Partitions,MediaLoaded,Status,Manufacturer | Format-Table -AutoSize }
Sec 'Volumes'; Try-Do vol { Get-Volume | Select-Object DriveLetter,FileSystemLabel,FileSystem,DriveType,Size,SizeRemaining,HealthStatus | Format-Table -AutoSize }
Try-Do part { Get-Partition | Where-Object DriveLetter | Select-Object DiskNumber,PartitionNumber,DriveLetter,Size | Format-Table -AutoSize }
Sec 'Network adapters'; Try-Do nic { Get-NetAdapter -Physical:$false | Select-Object Name,InterfaceDescription,Status,LinkSpeed,HardwareInterface,InterfaceType,MediaType,PhysicalMediaType | Format-Table -AutoSize }
Sec 'Battery'; Try-Do bat { Get-CimInstance Win32_Battery | Select-Object Name,EstimatedChargeRemaining,BatteryStatus,DesignCapacity,FullChargeCapacity | Format-List }
Sec 'Counter sets'; foreach($n in 'Processor Information','PhysicalDisk','Network Interface','GPU Engine','GPU Adapter Memory','GPU Process Memory','Memory'){ try { $s = Get-Counter -ListSet $n -ErrorAction Stop; $o.Add("$n : OK  instances=" + @($s.PathsWithInstances).Count + "  counters=" + @($s.Counter).Count) } catch { $o.Add("$n : MISSING ($($_.Exception.Message))") } }
Sec 'Sample counters'; Try-Do cnt { (Get-Counter -Counter '\Processor Information(_Total)\% Processor Time','\Processor Information(_Total)\% Processor Utility','\Processor Information(_Total)\Processor Frequency','\PhysicalDisk(*)\% Idle Time','\PhysicalDisk(*)\Disk Read Bytes/sec','\PhysicalDisk(*)\Avg. Disk sec/Transfer','\Network Interface(*)\Bytes Received/sec','\Network Interface(*)\Current Bandwidth' -SampleInterval 1 -MaxSamples 2 -ErrorAction SilentlyContinue)[1].CounterSamples | Select-Object Path,CookedValue | Format-Table -AutoSize -Wrap }
Try-Do gpu1 { (Get-Counter '\GPU Adapter Memory(*)\Dedicated Usage','\GPU Adapter Memory(*)\Shared Usage' -ErrorAction SilentlyContinue).CounterSamples | Select-Object InstanceName,Path,CookedValue | Format-Table -AutoSize -Wrap }
Try-Do gpu2 { $s=(Get-Counter '\GPU Engine(*)\Utilization Percentage' -ErrorAction SilentlyContinue).CounterSamples; "GPU Engine instances: $($s.Count)"; $s | Where-Object CookedValue -gt 0 | Select-Object -First 8 InstanceName,CookedValue | Format-Table -AutoSize -Wrap }
Sec 'Monitors / DPI'; Try-Do mon { Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Screen]::AllScreens | Select-Object DeviceName,Primary,Bounds,WorkingArea | Format-List }
Sec 'dotnet'; Try-Do dn { Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object Source; & (Join-Path $PSScriptRoot '..\..\EventLens\.tools\dotnet\dotnet.exe') --list-runtimes }
[IO.File]::WriteAllLines($Out, $o)
"Wrote $Out"
