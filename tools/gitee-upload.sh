#!/usr/bin/env bash
# 在 Gitee Go 流水线中把 GitHub Release 安装包补传到 Gitee 发行版
# 用法：VERSION=1.0.4 bash tools/gitee-upload.sh
set -euo pipefail

VERSION="${VERSION:?请设置 VERSION，例如 VERSION=1.0.4}"
VERSION="${VERSION#v}"
TAG="v${VERSION}"
PKG="LoRPoCTracker-${VERSION}-win-x64.zip"
REPO="${RELEASE_REPO:-wuyi12138/LoRPoCTracker}"
TOKEN="${RELEASE_TOKEN:-}"

if [ -z "${TOKEN}" ]; then
    echo "错误：RELEASE_TOKEN 未注入（长度为 0）"
    echo "当前环境变量中包含 TOKEN 的项："
    env | grep -i token | sed 's/=.*/=<已隐藏>/' || echo "  （无）"
    exit 1
fi
echo "RELEASE_TOKEN 已注入，长度 ${#TOKEN}"

echo "版本: ${TAG}  包: ${PKG}"

mkdir -p dist
if [ ! -f "dist/${PKG}" ]; then
    echo "下载安装包（走 ghfast 镜像）..."
    curl -fL --retry 3 --max-time 900 -o "dist/${PKG}" \
        "https://ghfast.top/https://github.com/Nohtaehp/LoRPoCTracker/releases/download/${TAG}/${PKG}"
fi
ls -lh "dist/${PKG}"

API="https://gitee.com/api/v5/repos/${REPO}"
BODY="详见 Release 页面：https://github.com/Nohtaehp/LoRPoCTracker/releases/tag/${TAG}"

HTTP=$(curl -s -o create.json -w "%{http_code}" -X POST "${API}/releases" \
    -H "Content-Type: application/json" \
    -d "{\"access_token\":\"${TOKEN}\",\"tag_name\":\"${TAG}\",\"name\":\"${TAG}\",\"target_commitish\":\"main\",\"body\":\"${BODY}\"}")
echo "创建发行版 HTTP ${HTTP}"
cat create.json || true

if [ "${HTTP}" = "200" ] || [ "${HTTP}" = "201" ]; then
    RID=$(sed -n 's/.*"id":\([0-9]\+\).*/\1/p' create.json | head -1)
else
    RID=$(curl -s "${API}/releases/tags/${TAG}?access_token=${TOKEN}" \
        | sed -n 's/.*"id":\([0-9]\+\).*/\1/p' | head -1)
fi

if [ -z "${RID}" ]; then
    echo "无法获取发行版 ID" >&2
    exit 1
fi
echo "发行版 ID: ${RID}"

HTTP2=$(curl -s -o upload.json -w "%{http_code}" --max-time 1800 \
    -X POST "${API}/releases/${RID}/attach_files" \
    -F "access_token=${TOKEN}" \
    -F "file=@dist/${PKG}")
echo "上传附件 HTTP ${HTTP2}"
cat upload.json || true

if [ "${HTTP2}" != "200" ] && [ "${HTTP2}" != "201" ]; then
    echo "上传失败" >&2
    exit 1
fi

echo "完成：https://gitee.com/${REPO}/releases/tag/${TAG}"
