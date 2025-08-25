# Test Direct hostPath Access for PluginLoader
# This script tests bidirectional file access using direct hostPath volumes

param(
    [switch]$Cleanup = $false
)

Write-Host "🧪 Testing Direct hostPath Access..." -ForegroundColor Green

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
        kubectl exec $podName -n design79-infrastructure -- ls -la $($dir.Path) 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "   ✅ Pod can access $($dir.Path) - $($dir.Purpose)" -ForegroundColor Green
        } else {
            Write-Host "   ❌ Pod cannot access $($dir.Path) - $($dir.Purpose)" -ForegroundColor Red
        }
    }
    
    # Test 2: Plugin Libraries - Check existing plugins
    Write-Host ""
    Write-Host "🔍 Test 2: Checking plugin libraries access..." -ForegroundColor Yellow
    
    $pluginList = kubectl exec $podName -n design79-infrastructure -- find /app/plugins -name "*.dll" -type f 2>$null
    if ($LASTEXITCODE -eq 0 -and ![string]::IsNullOrEmpty($pluginList)) {
        Write-Host "   ✅ Plugin DLLs found:" -ForegroundColor Green
        $pluginList -split "`n" | ForEach-Object { Write-Host "     $_" -ForegroundColor White }
    } else {
        Write-Host "   ⚠️  No plugin DLLs found (this is normal if no plugins are deployed yet)" -ForegroundColor Yellow
        # Show directory structure instead
        $pluginDirs = kubectl exec $podName -n design79-infrastructure -- ls -la /app/plugins 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "   📁 Plugin directory contents:" -ForegroundColor Cyan
            Write-Host $pluginDirs
        }
    }
    
    # Test 3: Input directory - Host to Pod file access
    Write-Host ""
    Write-Host "🔍 Test 3: Input directory (Host to Pod) file access..." -ForegroundColor Yellow
    
    $testFile = "test-input-$(Get-Date -Format 'yyyyMMdd-HHmmss').txt"
    $hostPath = "C:\temp\input\$testFile"
    $testContent = "Input file from host! $(Get-Date)"
    
    Write-Host "   - Creating input file on host: $hostPath" -ForegroundColor Cyan
    $testContent | Out-File -FilePath $hostPath -Encoding UTF8
    
    Start-Sleep -Seconds 1  # hostPath is immediate, but give a moment
    
    Write-Host "   - Reading input file from pod..." -ForegroundColor Cyan
    $podContent = kubectl exec $podName -n design79-infrastructure -- cat /app/input/$testFile 2>$null
    if ($LASTEXITCODE -eq 0 -and $podContent -like "*Input file from host*") {
        Write-Host "   ✅ Input directory (Host to Pod) access working!" -ForegroundColor Green
        Write-Host "   Content: $podContent" -ForegroundColor White
    } else {
        Write-Host "   ❌ Input directory (Host to Pod) access failed" -ForegroundColor Red
    }
    
    # Test 4: Output directory - Pod to Host file access
    Write-Host ""
    Write-Host "🔍 Test 4: Output directory (Pod → Host) file access..." -ForegroundColor Yellow
    
    $testFile2 = "test-output-$(Get-Date -Format 'yyyyMMdd-HHmmss').txt"
    $hostPath2 = "C:\temp\output\$testFile2"
    $testContent2 = "Output file from pod! $(Get-Date)"
    
    Write-Host "   - Creating output file from pod..." -ForegroundColor Cyan
    kubectl exec $podName -n design79-infrastructure -- sh -c "echo '$testContent2' > /app/output/$testFile2"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create output file from pod"
    }
    
    Start-Sleep -Seconds 1  # hostPath is immediate, but give a moment
    
    Write-Host "   - Reading output file from host: $hostPath2" -ForegroundColor Cyan
    if (Test-Path $hostPath2) {
        $hostContent = Get-Content $hostPath2 -Raw
        if ($hostContent -like "*Output file from pod*") {
            Write-Host "   ✅ Output directory (Pod to Host) access working!" -ForegroundColor Green
            Write-Host "   Content: $hostContent" -ForegroundColor White
        } else {
            Write-Host "   ❌ Output directory (Pod to Host) access failed (content mismatch)" -ForegroundColor Red
        }
    } else {
        Write-Host "   ❌ Output directory (Pod to Host) access failed (file not found)" -ForegroundColor Red
    }
    
    # Test 5: Plugin directory bidirectional access
    Write-Host ""
    Write-Host "🔍 Test 5: Plugin directory bidirectional access..." -ForegroundColor Yellow
    
    $testFile3 = "test-plugin-$(Get-Date -Format 'yyyyMMdd-HHmmss').txt"
    $hostPath3 = "C:\Users\Administrator\source\repos\Design80\Libraries\$testFile3"
    $testContent3 = "Plugin test file from host! $(Get-Date)"
    
    Write-Host "   - Creating test file on host in Libraries..." -ForegroundColor Cyan
    $testContent3 | Out-File -FilePath $hostPath3 -Encoding UTF8
    
    Start-Sleep -Seconds 1
    
    Write-Host "   - Reading file from pod..." -ForegroundColor Cyan
    $podContent3 = kubectl exec $podName -n design79-infrastructure -- cat /app/plugins/$testFile3 2>$null
    if ($LASTEXITCODE -eq 0 -and $podContent3 -like "*Plugin test file from host*") {
        Write-Host "   ✅ Plugin directory (Host to Pod) access working!" -ForegroundColor Green
    } else {
        Write-Host "   ❌ Plugin directory (Host to Pod) access failed" -ForegroundColor Red
    }
    
    Write-Host ""
    Write-Host "🎉 Direct hostPath Access Testing Completed!" -ForegroundColor Green
    
    Write-Host ""
    Write-Host "📊 Summary:" -ForegroundColor Cyan
    Write-Host "   🔧 Plugin Libraries: C:\Users\Administrator\source\repos\Design80\Libraries <-> /app/plugins" -ForegroundColor White
    Write-Host "   📥 Input Files: C:\temp\input -> /app/input" -ForegroundColor White
    Write-Host "   📤 Output Files: /app/output -> C:\temp\output" -ForegroundColor White
    Write-Host "   ⚡ Access Type: Direct hostPath (immediate sync)" -ForegroundColor White
    
    # Cleanup option
    if ($Cleanup) {
        Write-Host ""
        Write-Host "🧹 Cleaning up test files..." -ForegroundColor Yellow
        
        if (Test-Path $hostPath) { Remove-Item $hostPath -Force }
        if (Test-Path $hostPath2) { Remove-Item $hostPath2 -Force }
        if (Test-Path $hostPath3) { Remove-Item $hostPath3 -Force }
        
        Write-Host "   ✅ Test files cleaned up" -ForegroundColor Green
    } else {
        Write-Host ""
        Write-Host "💡 To clean up test files, run: .\scripts\test-hostpath-access.ps1 -Cleanup" -ForegroundColor Cyan
    }

} catch {
    Write-Host ""
    Write-Host "❌ Test failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "🔍 Troubleshooting:" -ForegroundColor Yellow
    Write-Host "   - Check PluginLoader: kubectl get pods -l app=design79-pluginloader-processor -n design79-infrastructure" -ForegroundColor White
    Write-Host "   - Check mounts: kubectl describe pod $podName -n design79-infrastructure" -ForegroundColor White
    Write-Host "   - Check logs: kubectl logs $podName -n design79-infrastructure" -ForegroundColor White
    exit 1
}
