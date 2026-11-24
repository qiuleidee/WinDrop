#!/usr/bin/env bash
set -euo pipefail

echo "正在调用 macOS 系统原生 shortcuts 工具进行 Apple 签名..."
shortcuts sign --mode anyone --input "发送到电脑.shortcut" --output "发送到电脑_已签名.shortcut"
shortcuts sign --mode anyone --input "从电脑接收.shortcut" --output "从电脑接收_已签名.shortcut"

echo "签名完成！请将 *_已签名.shortcut 通过隔空投送或 iCloud 链接直接导入任意 iPhone。"