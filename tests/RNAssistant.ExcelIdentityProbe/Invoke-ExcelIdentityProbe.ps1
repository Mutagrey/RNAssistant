param(
    [Parameter(Mandatory = $true)][long]$Hwnd,
    [int]$WorkbookIndex = 0,
    [string]$ClientLabel = "external-probe"
)

$ErrorActionPreference = "Stop"
if ($env:OS -ne "Windows_NT" -or -not [Environment]::Is64BitProcess -or
    [Threading.Thread]::CurrentThread.GetApartmentState() -ne "STA") {
    throw "Run with Windows PowerShell x64 -STA on the Windows qualification machine."
}
$probeAssembly = Join-Path $PSScriptRoot "bin\Debug\net48\RNAssistant.ExcelIdentityProbe.dll"
if (-not (Test-Path $probeAssembly)) { throw "Build the diagnostic project in Debug first. See README.md." }
Add-Type -Path $probeAssembly
$application = [RNAssistant.ExcelIdentityProbe.ExcelProbeTarget]::ResolveApplication($Hwnd)
$excelProcessId = [RNAssistant.ExcelIdentityProbe.ExcelProbeTarget]::ProcessId($Hwnd)
$excelProcess = Get-Process -Id $excelProcessId
$excelStartedUtc = $excelProcess.StartTime.ToUniversalTime().ToString("o")
$clientProcessId = [Diagnostics.Process]::GetCurrentProcess().Id
$excelVersion = [string]$application.Version

if ($WorkbookIndex -eq 0) {
    for ($index = 1; $index -le $application.Workbooks.Count; $index++) {
        $item = $application.Workbooks.Item($index)
        [ordered]@{ index = $index; name = [string]$item.Name; fullName = [string]$item.FullName } |
            ConvertTo-Json -Compress
    }
    return
}
if ($WorkbookIndex -lt 1 -or $WorkbookIndex -gt $application.Workbooks.Count) {
    throw "WorkbookIndex does not identify an open workbook. List candidates first."
}

# Bind once. No later path/name/index lookup can replace this workbook reference.
$workbook = $application.Workbooks.Item($WorkbookIndex)
$savedBeforeBind = [bool]$workbook.Saved
$lease = $null
try {
    $lease = [RNAssistant.ExcelIdentityProbe.ComIdentityLease]::Create($workbook)
    $savedAfterBind = [bool]$workbook.Saved
    $scenario = "initial"
    do {
        $row = [ordered]@{
            schema = 1; utc = [DateTime]::UtcNow.ToString("o"); label = $ClientLabel; scenario = $scenario
            clientProcessId = $clientProcessId; ownerThread = [Threading.Thread]::CurrentThread.ManagedThreadId
            excelProcessId = $excelProcessId; excelStartedUtc = $excelStartedUtc; excelVersion = $excelVersion
            initialCandidate = $lease.Initial.Candidate; savedBeforeBind = $savedBeforeBind; savedAfterBind = $savedAfterBind
            status = "unavailable"
        }
        try {
            $isOpen = $false
            for ($index = 1; $index -le $application.Workbooks.Count; $index++) {
                if ([RNAssistant.ExcelIdentityProbe.ExcelProbeTarget]::SameLocalObject($workbook, $application.Workbooks.Item($index))) {
                    $isOpen = $true
                    break
                }
            }
            if ($isOpen) {
                $row.savedBeforeRead = [bool]$workbook.Saved
                $row.name = [string]$workbook.Name
                $row.fullName = [string]$workbook.FullName
                $sample = $lease.ReadAgain()
                $row.observedCandidate = $sample.Candidate
                $row.observedIpid = $sample.Ipid
                $row.sameCandidate = $sample.Candidate -ceq $lease.Initial.Candidate
                $row.savedAfterRead = [bool]$workbook.Saved
                $row.status = "observed"
            } else {
                $row.status = "closed"
            }
        } catch {
            $row.error = $_.Exception.Message
        }
        $row | ConvertTo-Json -Compress
        if ($row.status -eq "unavailable") { throw "Snapshot failed; see unavailable record. No qualification pass recorded." }
        $scenario = Read-Host "Scenario label after the next manual action (q to release and exit)"
    } while ($scenario -cne "q")
} finally {
    if ($null -ne $lease) {
        $lease.Dispose()
        [ordered]@{ schema = 1; status = "released"; clientProcessId = $clientProcessId; label = $ClientLabel } |
            ConvertTo-Json -Compress
    }
}
