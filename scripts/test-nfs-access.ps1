# Test NFS Bidirectional Access for PluginLoader
# This script tests that files can be read and written between host and pods

param(
    [switch]$Cleanup = $false
)

Write-Host "🧪 Testing NFS Bidirectional Access..." -ForegroundColor Green

$ErrorActionPreference = "Stop"

try {
    # Get PluginLoader pod name
    $podName = kubectl get pods -l app=design79-pluginloader-processor -n design79-infrastructure -o jsonpath="{.items[0].metadata.name}" 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrEmpty($podName)) {
        throw "PluginLoader pod not found. Make sure the deployment is running."
    }
    
    Write-Host "📍 Using PluginLoader pod: $podName" -ForegroundColor Cyan
    
    # Test 1: Check if all mounted directories are accessible from pod
    Write-Host ""
    Write-Host "🔍 Test 1: Checking mounted directories access from pod..." -ForegroundColor Yellow

    $directories = @(
        @{Path="/app/plugins"; Host="C:\Users\Administrator\source\repos\Design80\Libraries"; Purpose="Plugin assemblies"},
        @{Path="/app/input"; Host="C:\temp\input"; Purpose="Input files (Host → Pod)"},
        @{Path="/app/output"; Host="C:\temp\output"; Purpose="Output files (Pod → Host)"}
    )

    foreach ($dir in $directories) {
        kubectl exec $podName -n design79-infrastructure -- ls -la $dir.Path 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "   ✅ Pod can access $($dir.Path) - $($dir.Purpose)" -ForegroundColor Green
        } else {
            Write-Host "   ❌ Pod cannot access $($dir.Path) - $($dir.Purpose)" -ForegroundColor Red
        }
    }
    
    # Test 2: Input directory - Host → Pod file access
    Write-Host ""
    Write-Host "🔍 Test 2: Input directory (Host → Pod) file access..." -ForegroundColor Yellow

    # Ensure input directory exists
    if (!(Test-Path "C:\temp\input" -PathType Container)) {
        New-Item -Path "C:\temp\input" -ItemType Directory -Force | Out-Null
        Write-Host "   📁 Created input directory: C:\temp\input" -ForegroundColor Cyan
    }

    $testFile = "test-input-$(Get-Date -Format 'yyyyMMdd-HHmmss').txt"
    $hostPath = "C:\temp\input\$testFile"
    $testContent = "Input file from host! $(Get-Date)"

    Write-Host "   - Creating input file on host: $hostPath" -ForegroundColor Cyan
    $testContent | Out-File -FilePath $hostPath -Encoding UTF8

    Start-Sleep -Seconds 2  # Give NFS time to sync

    Write-Host "   - Reading input file from pod..." -ForegroundColor Cyan
    $podContent = kubectl exec $podName -n design79-infrastructure -- cat /app/input/$testFile 2>$null
    if ($LASTEXITCODE -eq 0 -and $podContent -like "*Input file from host*") {
        Write-Host "   ✅ Input directory (Host → Pod) access working!" -ForegroundColor Green
        Write-Host "   Content: $podContent" -ForegroundColor White
    } else {
        Write-Host "   ❌ Input directory (Host → Pod) access failed" -ForegroundColor Red
    }
    
    # Test 3: Output directory - Pod → Host file access
    Write-Host ""
    Write-Host "🔍 Test 3: Output directory (Pod → Host) file access..." -ForegroundColor Yellow

    # Ensure output directory exists
    if (!(Test-Path "C:\temp\output" -PathType Container)) {
        New-Item -Path "C:\temp\output" -ItemType Directory -Force | Out-Null
        Write-Host "   📁 Created output directory: C:\temp\output" -ForegroundColor Cyan
    }

    $testFile2 = "test-output-$(Get-Date -Format 'yyyyMMdd-HHmmss').txt"
    $hostPath2 = "C:\temp\output\$testFile2"
    $testContent2 = "Output file from pod! $(Get-Date)"

    Write-Host "   - Creating output file from pod..." -ForegroundColor Cyan
    kubectl exec $podName -n design79-infrastructure -- sh -c "echo '$testContent2' > /app/output/$testFile2"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create output file from pod"
    }

    Start-Sleep -Seconds 2  # Give NFS time to sync

    Write-Host "   - Reading output file from host: $hostPath2" -ForegroundColor Cyan
    if (Test-Path $hostPath2) {
        $hostContent = Get-Content $hostPath2 -Raw
        if ($hostContent -like "*Output file from pod*") {
            Write-Host "   ✅ Output directory (Pod → Host) access working!" -ForegroundColor Green
            Write-Host "   Content: $hostContent" -ForegroundColor White
        } else {
            Write-Host "   ❌ Output directory (Pod → Host) access failed (content mismatch)" -ForegroundColor Red
        }
    } else {
        Write-Host "   ❌ Output directory (Pod → Host) access failed (file not found)" -ForegroundColor Red
    }
    
    # Test 4: Check plugin structure
    Write-Host ""
    Write-Host "🔍 Test 4: Checking plugin structure..." -ForegroundColor Yellow
    
    $pluginStructure = kubectl exec $podName -n design79-infrastructure -- find /app/plugins -name "*.dll" -type f 2>$null
    if ($LASTEXITCODE -eq 0 -and ![string]::IsNullOrEmpty($pluginStructure)) {
        Write-Host "   ✅ Plugin DLLs found:" -ForegroundColor Green
        $pluginStructure -split "`n" | ForEach-Object { Write-Host "     $_" -ForegroundColor White }
    } else {
        Write-Host "   ⚠️  No plugin DLLs found (this is normal if no plugins are deployed yet)" -ForegroundColor Yellow
    }
    
    # Test 5: Test directory creation from pod
    Write-Host ""
    Write-Host "🔍 Test 5: Directory creation from pod..." -ForegroundColor Yellow
    
    $testDir = "test-output-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    kubectl exec $podName -n design79-infrastructure -- mkdir -p /app/plugins/$testDir
    if ($LASTEXITCODE -eq 0) {
        $hostDirPath = "C:\Users\Administrator\source\repos\Design80\Libraries\$testDir"
        Start-Sleep -Seconds 2
        if (Test-Path $hostDirPath -PathType Container) {
            Write-Host "   ✅ Directory creation working!" -ForegroundColor Green
            Write-Host "   Created: $hostDirPath" -ForegroundColor White
        } else {
            Write-Host "   ❌ Directory creation failed" -ForegroundColor Red
        }
    } else {
        Write-Host "   ❌ Failed to create directory from pod" -ForegroundColor Red
    }
    
    Write-Host ""
    Write-Host "🎉 NFS Access Testing Completed!" -ForegroundColor Green
    
    # Cleanup option
    if ($Cleanup) {
        Write-Host ""
        Write-Host "🧹 Cleaning up test files..." -ForegroundColor Yellow
        
        if (Test-Path $hostPath) { Remove-Item $hostPath -Force }
        if (Test-Path $hostPath2) { Remove-Item $hostPath2 -Force }
        if (Test-Path "C:\Users\Administrator\source\repos\Design80\Libraries\$testDir") { 
            Remove-Item "C:\Users\Administrator\source\repos\Design80\Libraries\$testDir" -Recurse -Force 
        }
        
        Write-Host "   ✅ Test files cleaned up" -ForegroundColor Green
    } else {
        Write-Host ""
        Write-Host "💡 To clean up test files, run: .\scripts\test-nfs-access.ps1 -Cleanup" -ForegroundColor Cyan
    }

} catch {
    Write-Host ""
    Write-Host "❌ Test failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "🔍 Troubleshooting:" -ForegroundColor Yellow
    Write-Host "   - Check NFS server: kubectl get pods -l app=design79-nfs-server -n design79-infrastructure" -ForegroundColor White
    Write-Host "   - Check PluginLoader: kubectl get pods -l app=design79-pluginloader-processor -n design79-infrastructure" -ForegroundColor White
    Write-Host "   - Check mounts: kubectl describe pod $podName -n design79-infrastructure" -ForegroundColor White
    exit 1
}
