# FastTunnel 服务端薄镜像（形态与 AMZS 的 AMZS.Dist/Dockerfile 一致）
#
# 注意：构建上下文是 dotnet publish 的产物目录，不是这个源码目录。
# CentOS 7 上没有 .NET SDK，也不做容器内构建，所以编译在本机做完，再把这份 Dockerfile
# 与 docker-compose.yml 一起 scp 到产物目录里 build。具体几条 scp 见 fasttunnel.sh 头部。
#
# 基础镜像：私有仓库里已备好 ASP.NET Core 10 运行时，生产网可直接拉取
FROM xuefeng0909/aspnetcore-runtime:10.0.20260924

WORKDIR /app
COPY . /app

# 单端口（2026-10-10 定）：1270 = 客户端控制通道 + 客户端回拨数据通道 + 访客 HTTP 入口，
# TLS 由同机前置 Nginx 终结后回源 127.0.0.1:1270。旧的访客专用 8080 已不再监听。
# 真正生效的监听由 config/appsettings.json 根节点的 urls 决定，这里只是标注
EXPOSE 1270

ENV TZ="Asia/Shanghai"

# 产物和镜像里都没有 config/appsettings.Production.json（csproj 里那个
# KeepProdConfigOutOfArtifacts 已把它从 Content/None 移除，凭证不进镜像层）。
# 真实配置由 docker-compose.yml 把宿主 /opt/conf/fasttunnel 挂到 /app/config。
# 挂载目录为空时容器会启动即退出并报缺 config/appsettings.json —— 属预期表现，不是镜像坏了。
ENTRYPOINT ["dotnet","FastTunnel.Server.dll"]
