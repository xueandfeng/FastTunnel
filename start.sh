#!/bin/bash
# 把宿主机挂载进来的配置拷进容器，让 $DATA_DIR/config/ 成为配置的权威来源。
#
# 为什么用 cp 而不是 mv（原实现是 mv -n）：mv 会把源文件搬走并删掉宿主上的副本，
# 于是 $DATA_DIR/config/ 首次启动后变空，配置只剩在容器可写层里；而 docker-deploy.sh
# 每次启动都是 docker rm -f + docker run，重建后挂载目录是空的 → 服务端退回镜像里的
# 占位符配置，表现为“现网客户端突然全部连不上”。真实凭证 appsettings.Production.json
# 正是被这样“用一次就没”的。
#
# 为什么用 -f 覆盖而不是 -n 跳过：/app/config 每次重建都来自镜像，若跳过同名文件，
# 宿主机上改过的 appsettings.json（比如改 urls、换 WebDomain）永远不会生效，只能靠
# appsettings.Production.json 覆盖 —— 而部署脚本的提示恰恰是让人去改那个同名文件。
# 代价：镜像升级带来的 appsettings.json 新字段不会自动出现在已有部署上，升级时把新字段
# 手工补进宿主机那份（缺文件由镜像兜底，所以不会漏 config 目录）。
if [ -n "$(ls -A /vols/config 2>/dev/null)" ]; then
    cp -rf /vols/config/. /app/config/
fi
dotnet FastTunnel.Server.dll
