# FastTunnel 客户端薄镜像（形态与服务端那份一致：本机 publish → 产物目录 build）
#
# scp 清单与部署命令（含一台机器多实例的用法）见同目录的 fasttunnelclient.sh / docker-compose.yml。
#
# 同机多实例共用这一个镜像：上下文与 Dockerfile 都相同，不必为每份额外 build。
#
# 基础镜像与服务端用同一个：aspnetcore-runtime 是 base runtime 的超集，控制台程序照样跑得起来，
# 少备一个基础镜像就少一处版本对不齐的风险（镜像版本要跟 Directory.Build.props 的 <TargetFramework> 对齐）
FROM xuefeng0909/aspnetcore-runtime:10.0.20260924

WORKDIR /app
COPY . /app

ENV TZ="Asia/Shanghai"

# 客户端的配置在产物根目录（不是 config/ 子目录），所以 docker-compose.yml 里是单文件挂到
# /app/appsettings.Production.json。不能整目录挂到 /app：那会把镜像里的 dll 一起遮掉。
# 仓库根那本 appsettings.json 是全 change-me-* 模板，宿主没放覆盖文件时客户端会反复连一个
# 不存在的主机名（宁连不上也不偷用仓库里的值，这是故意的）。
ENTRYPOINT ["dotnet","FastTunnel.Client.dll"]
