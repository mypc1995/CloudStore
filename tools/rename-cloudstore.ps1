# 一次性改名脚本：命名空间 R2Manager.* -> CloudStore.*，R2Settings -> StorageSettings，R2Service -> StorageService
# 注意：不动 SecretProtector 的 entropy（"R2Manager.v1.secret"）与配置目录名（%APPDATA%\R2Manager），
#      否则用户已保存的密钥将无法解密、配置会丢失。
$ErrorActionPreference = 'Stop'

$root = 'D:\Software\R2UP\src\R2Manager'
$files = Get-ChildItem -Path $root -Recurse -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }

$map = [ordered]@{
    'R2Manager.Core' = 'CloudStore.Core'
    'R2Manager.UI'   = 'CloudStore.UI'
    'namespace R2Manager;' = 'namespace CloudStore;'
    'R2Manager.app.ico' = 'CloudStore.app.ico'
    'R2Settings' = 'StorageSettings'
    'R2Service'  = 'StorageService'
}

$utf8 = New-Object Text.UTF8Encoding($false)
$changed = 0

foreach ($file in $files) {
    $text = [IO.File]::ReadAllText($file.FullName, [Text.Encoding]::UTF8)
    $original = $text
    foreach ($pair in $map.GetEnumerator()) {
        $text = $text.Replace($pair.Key, $pair.Value)
    }
    if ($text -ne $original) {
        [IO.File]::WriteAllText($file.FullName, $text, $utf8)
        $changed++
        Write-Host "  改名: $($file.FullName.Substring($root.Length + 1))"
    }
}

Write-Host "共修改 $changed 个文件。"
