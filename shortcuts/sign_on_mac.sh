#!/usr/bin/env bash
set -euo pipefail

echo "正在转换二进制 Plist 并调用 macOS 系统原生 shortcuts 工具进行 Apple 签名..."
plutil -convert binary1 "$PWD/发送到电脑.shortcut" -o "$PWD/send_bin.shortcut"
plutil -convert binary1 "$PWD/从电脑接收.shortcut" -o "$PWD/recv_bin.shortcut"
shortcuts sign --mode anyone --input "$PWD/send_bin.shortcut" --output "$PWD/发送到电脑_已签名.shortcut"
shortcuts sign --mode anyone --input "$PWD/recv_bin.shortcut" --output "$PWD/从电脑接收_已签名.shortcut"
rm -f "$PWD/send_bin.shortcut" "$PWD/recv_bin.shortcut"

echo "签名完成！请将 *_已签名.shortcut 通过隔空投送或 iCloud 链接直接导入任意 iPhone。"