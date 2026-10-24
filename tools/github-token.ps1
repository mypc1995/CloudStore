# 从 Windows 凭据管理器里取出 GitHub Desktop 存的 OAuth 令牌。
#
# 只用于本机向 GitHub 建仓 / 推送，绝不打印明文：
# 成功时把令牌写进 .git-credentials（受 .gitignore 保护）或者直接返回给调用方。

$ErrorActionPreference = 'Stop'

Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class CredReader {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CREDENTIAL {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CredRead(string target, int type, int flags, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern void CredFree(IntPtr cred);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CredDelete(string target, int type, int flags);
}
"@

function Get-GitHubToken {
    $targets = @(
        'git:https://github.com',
        'GitHub - https://api.github.com/mypc1995'
    )

    foreach ($target in $targets) {
        $ptr = [IntPtr]::Zero

        if ([CredReader]::CredRead($target, 1, 0, [ref]$ptr)) {
            try {
                $cred = [Runtime.InteropServices.Marshal]::PtrToStructure(
                    $ptr, [type][CredReader+CREDENTIAL])

                $size = $cred.CredentialBlobSize
                $bytes = New-Object byte[] $size
                [Runtime.InteropServices.Marshal]::Copy($cred.CredentialBlob, $bytes, 0, $size)
                $text = [System.Text.Encoding]::UTF8.GetString($bytes)

                if ($text -match '^\s*$') { continue }

                # GitHub Desktop 早期版本会存成 JSON，取里面的 oauth_token
                if ($text.TrimStart().StartsWith('{')) {
                    try {
                        $json = $text | ConvertFrom-Json
                        if ($json.oauth_token) { return $json.oauth_token }
                        if ($json.token) { return $json.token }
                    }
                    catch { }
                }

                return $text.Trim()
            }
            finally {
                [CredReader]::CredFree($ptr)
            }
        }
    }

    return $null
}

$token = Get-GitHubToken

if (-not $token) {
    Write-Error '没找到 GitHub 凭据'
    exit 1
}

# 只回报长度，绝不把令牌打到控制台/日志里
Write-Host "已取到 GitHub 令牌，长度 $($token.Length) 字符"

$env:GITHUB_TOKEN = $token
