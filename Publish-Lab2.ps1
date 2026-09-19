param([ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'

# Chạy tại bất kỳ thư mục nào. Kết quả nằm trong artifacts/ (không đưa lên Git).
foreach ($component in @('Server', 'Client')) {
    $projectPath = Join-Path $PSScriptRoot "src/Chat.$component/Chat.$component.csproj"
    $publishPath = Join-Path $PSScriptRoot "artifacts/Lab2Chat-$component-$Runtime"
    & dotnet publish $projectPath -c Release -r $Runtime --self-contained true `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false `
        -o $publishPath
    if ($LASTEXITCODE -ne 0) { throw "Publish $component thất bại." }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LAB2.md') -Destination $publishPath
    Compress-Archive -LiteralPath $publishPath -DestinationPath "$publishPath.zip" -Force
    Write-Output "Đã tạo $publishPath.zip"
}
