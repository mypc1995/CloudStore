"""重新打包发布用的 7z：安装程序 + 便携版 + 使用说明 + 校验码 + 两张截图。

用法：
    tools\\.venv7z\\Scripts\\python.exe tools\\pack-7z.py

venv 是专门给这个脚本用的（py7zr 依赖的原生模块在 Python 3.15 上编不过，
所以用 uv 管的 3.12 建了一个隔离环境）。重建方式：

    py -3.12 -m venv tools\\.venv7z
    tools\\.venv7z\\Scripts\\python.exe -m pip install py7zr
"""

import os
import py7zr

DIST = r"D:\Software\R2UP\dist"
OUT = os.path.join(DIST, "dist.7z")
FILES = [
    "云存储管家-安装程序.exe",
    "云存储管家.exe",
    "使用说明.txt",
    "SHA256.txt",
    "1.png",
    "2.png",
]


def main() -> None:
    missing = [name for name in FILES if not os.path.exists(os.path.join(DIST, name))]
    if missing:
        raise SystemExit("缺少文件：" + "、".join(missing))

    tmp = OUT + ".tmp"

    with py7zr.SevenZipFile(tmp, "w", filters=[{"id": py7zr.FILTER_LZMA2, "preset": 7}]) as archive:
        for name in FILES:
            archive.write(os.path.join(DIST, name), arcname=name)

    os.replace(tmp, OUT)

    size = os.path.getsize(OUT)
    print(f"已生成 {OUT}（{size / 1024 / 1024:.1f} MB）")

    with py7zr.SevenZipFile(OUT, "r") as archive:
        for name in archive.getnames():
            info = archive.getinfo(name)
            print(f"  {name}  {info.uncompressed / 1024 / 1024:.1f} MB")


if __name__ == "__main__":
    main()
