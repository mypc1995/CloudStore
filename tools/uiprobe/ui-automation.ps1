param(
    [Parameter(Mandatory)]
    [string] $WindowTitle,

    [Parameter(Mandatory)]
    [string] $ElementName
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement

$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $WindowTitle)

$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)

if (-not $win) {
    throw "没找到窗口 '$WindowTitle'"
}

$nameCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $ElementName)

$el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)

if (-not $el) {
    Write-Host "窗口里找不到名为 '$ElementName' 的元素，列出可见元素："
    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($x in $all) {
        $n = $x.Current.Name
        if ($n) { Write-Host "  [$($x.Current.ControlType.ProgrammaticName)] $n" }
    }
    throw '没找到目标元素'
}

Write-Host "找到元素: [$($el.Current.ControlType.ProgrammaticName)] $($el.Current.Name)"

$invoke = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)

if ($invoke) {
    $invoke.Invoke()
    Write-Host '已通过 InvokePattern 触发'
}
else {
    $sel = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    if ($sel) {
        $sel.Select()
        Write-Host '已通过 SelectionItemPattern 选中（需要回车/空格激活）'
    }
    else {
        throw '该元素不支持 Invoke / SelectionItem'
    }
}
