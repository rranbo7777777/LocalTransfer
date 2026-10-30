# UIA 驱动库：查找窗口/控件、点击按钮、读取文本、截图。
# 用于 LocalTransfer Windows 端的界面自检。
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

# 逐进程 DPI 感知：否则截图坐标被虚拟化，分辨率会漂移。
$signature = '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();'
Add-Type -MemberDefinition $signature -Name DpiHelper -Namespace Win32 | Out-Null
[Win32.DpiHelper]::SetProcessDPIAware() | Out-Null

$script:ShotDir = Join-Path $PSScriptRoot '..\..\自检截图'
New-Item -ItemType Directory -Force -Path $script:ShotDir | Out-Null

function Find-Window {
    param([string]$Name, [int]$TimeoutMs = 5000)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $condition)
        if ($window) { return $window }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Find-Element {
    param($Root, [string]$Name, $ControlType = $null, [int]$TimeoutMs = 4000)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        $conditions = New-Object System.Collections.Generic.List[System.Windows.Automation.Condition]
        $conditions.Add((New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
        if ($ControlType) {
            $conditions.Add((New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)))
        }
        $condition = $conditions[0]
        if ($conditions.Count -gt 1) {
            $condition = New-Object System.Windows.Automation.AndCondition($conditions.ToArray())
        }
        $element = $Root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($element) { return $element }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Invoke-UiButton {
    param($Root, [string]$Name, [int]$TimeoutMs = 4000)
    $button = Find-Element $Root $Name ([System.Windows.Automation.ControlType]::Button) $TimeoutMs
    if (-not $button) { throw "按钮未找到: $Name" }
    $pattern = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Start-Sleep -Milliseconds 500
}

function Get-UiText {
    param($Root)
    # 抓取窗口内全部 Text/Edit 元素文本，便于断言。
    $all = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $texts = @()
    foreach ($element in $all) {
        $programmatic = $element.Current.ControlType.ProgrammaticName
        if ($programmatic -match 'Text|Edit') {
            if ($element.Current.Name) { $texts += $element.Current.Name }
        }
    }
    return ($texts -join "`n")
}

function Focus-Window {
    param($Window)
    if (-not $Window) { return }
    try {
        $state = $Window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        $state.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Maximized)
    } catch { }
    try { $Window.SetFocus() } catch { }
    Start-Sleep -Milliseconds 600
}

function Save-UiShot {
    param([string]$Name)
    $path = Join-Path $script:ShotDir ($Name + '.png')
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bitmap = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()
    Write-Output "SHOT $path"
    return $path
}
