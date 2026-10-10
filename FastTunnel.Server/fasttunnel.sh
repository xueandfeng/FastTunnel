#!/usr/bin/env bash
# FastTunnel 服务端部署（服务器上执行，套路与 AMZS 的 dist.sh 一致：产物 + 薄镜像 + 重启容器）
#
# 本机在仓库根出产物，然后传这几样（本文件与 Dockerfile / docker-compose.yml 都在 FastTunnel.Server\ 下维护）：
#   dotnet publish FastTunnel.Server -c Release -o publish
#   scp -r publish/*                                                       root@服务器:/opt/dotnet/fasttunnel/
#   scp FastTunnel.Server/Dockerfile FastTunnel.Server/docker-compose.yml  root@服务器:/opt/dotnet/fasttunnel/
#   scp FastTunnel.Server/fasttunnel.sh                                      root@服务器:/opt/dotnet/fasttunnel/
#   scp publish/config/appsettings.json                                    root@服务器:/opt/conf/fasttunnel/
#   scp FastTunnel.Server/config/appsettings.Production.json               root@服务器:/opt/conf/fasttunnel/
# 服务器上只跑一条命令：
#   sh /opt/dotnet/fasttunnel/fasttunnel.sh
#
# 首次部署先确认 docker 开机自启（否则机器重启后 daemon 不起，compose 里的 restart: always 也无从谈起）：
#   systemctl enable --now docker
# SELinux 为 enforcing 时（getenforce 查），docker-compose.yml 里那两个挂载末尾要加 :z。
set -e

cd /opt/dotnet/fasttunnel
# CentOS 7 上可能是 docker-compose 1.x 独立版，也可能是 docker compose 插件
docker compose version >/dev/null 2>&1 && C='docker compose' || C='docker-compose'

# 真实凭证只在宿主机这份目录里（Token / JWT 密钥 / 管理口令 / urls），不进镜像
test -f /opt/conf/fasttunnel/appsettings.json || { echo '缺 /opt/conf/fasttunnel/appsettings.json（从产物 config/ 拷一份）'; exit 1; }
test -f /opt/conf/fasttunnel/appsettings.Production.json || { echo '缺 /opt/conf/fasttunnel/appsettings.Production.json'; exit 1; }
# 产物里不该有凭证（csproj 已排除）；出现了说明上传的是旧产物，它会被 COPY 烧进镜像层
test ! -f config/appsettings.Production.json || { echo '产物里有 config/appsettings.Production.json，重新 publish 再传'; exit 1; }

echo '停止 fasttunnel_server 容器'
$C down
echo '构建 fasttunnel_server 镜像'
$C up -d --build
sleep 5
echo '--- 启动日志（看 Now listening on 是不是只有一条 1270，有没有 [SafetyCheck] 告警）---'
docker logs --tail 50 fasttunnel_server
# 桥接形态下宿主机侧到底发布了哪些端口、绑在哪个地址上，看这一行（host 网络下它是空的）。
# 单端口形态只期望一条 0.0.0.0:1270->1270/tcp；出现 8080 那一条说明容器里读的 urls 还是双端口
#（先查 /opt/conf/fasttunnel/appsettings.Production.json 是否已同步）。
echo '--- 端口映射 ---'
docker port fasttunnel_server
