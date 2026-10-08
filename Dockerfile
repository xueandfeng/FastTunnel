#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.
#
# 运行时版本必须与 Directory.Build.props 里的 <TargetFramework> 保持一致
# （当前为 net10.0）。改成其它版本时同步修改下面两个镜像标签。

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
# 控制通道（WebSocket）与 HTTP 穿透端口，实际端口以 config/appsettings.json 为准
EXPOSE 1270
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["FastTunnel.Server/FastTunnel.Server.csproj", "FastTunnel.Server/"]
COPY ["FastTunnel.Core/FastTunnel.Core.csproj", "FastTunnel.Core/"]
# 不再 COPY FastTunnel.Api：它不在 FastTunnel.sln 里、Server 也没 ProjectReference 它，
# 属于 net7.0 遗留项目；把它列进这里只会让人误以为服务端依赖它。
RUN dotnet restore "FastTunnel.Server/FastTunnel.Server.csproj"
COPY . .
WORKDIR "/src/FastTunnel.Server"
RUN dotnet build "FastTunnel.Server.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "FastTunnel.Server.csproj" -c Release -o /app/publish

FROM base AS final
#RUN mkdir -p /vols
WORKDIR /app
COPY --from=publish /app/publish/config /vols/config
COPY --from=publish /app/publish .
COPY ./start.sh .
ENTRYPOINT ["/bin/bash","./start.sh"]
#ENTRYPOINT ["dotnet", "FastTunnel.Server.dll"]
