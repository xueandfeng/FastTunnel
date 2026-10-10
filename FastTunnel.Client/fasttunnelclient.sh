#!/usr/bin/env bash
# FastTunnel 客户端部署（内网那几台机器上执行，与服务端同一套路：产物 + 薄镜像 + compose）
#
# 本机在仓库根出产物，然后传四个东西（本文件与 Dockerfile / docker-compose.yml 都在 FastTunnel.Client\ 下维护）：
#   dotnet publish FastTunnel.Client -c Release -o publish-client
#   scp -r publish-client/*                                                root@客户端机:/opt/dotnet/fasttunnel-client/
#   scp FastTunnel.Client/Dockerfile FastTunnel.Client/docker-compose.yml   root@客户端机:/opt/dotnet/fasttunnel-client/
#   scp FastTunnel.Client/fasttunnelclient.sh                              root@客户端机:/opt/dotnet/fasttunnel-client/
#   scp FastTunnel.Client/appsettings.Production.json                      root@客户端机:/opt/conf/fasttunnel-client/
# 客户端机上只跑一条命令：
#   sh /opt/dotnet/fasttunnel-client/fasttunnelclient.sh
#
# 一台机器上要多开几份就把实例名当参数（产物/Dockerfile/compose 都只有一份，不用复制目录）：
#   sh fasttunnelclient.sh                   # 默认实例 main：容器 fasttunnel-client、配置 /opt/conf/fasttunnel-client/
#   sh fasttunnelclient.sh client-b         # 第二份：容器 fasttunnel-client-client-b、配置 /opt/conf/fasttunnel-client/client-b/
#   sh fasttunnelclient.sh --list           # 看本机都有哪些实例、在不在跑
#   sh fasttunnelclient.sh --logs client-b  # 只看某实例最近 50 行
#   sh fasttunnelclient.sh --stop client-b  # 只停某实例（down 只作用于它自己的 project）
#   sh fasttunnelclient.sh --all            # 把所有实例滚一遍（镜像已有就纯复用）
#   sh fasttunnelclient.sh --rebuild        # 重传 dll 后重建镜像，再加 --all 就把所有实例都到新镜像上
# 镜像只有一份：哪个实例先跑就哪个把它建出来（本地没镜像时才 build），之后开的实例直接复用，
# 不再重跑 build。代价是“产物换了但镜像不会自动跟进”，所以重传 publish-client/* 后必须显式 --rebuild。
# 多台机器各一份时全部用默认实例名即可，路径与单实例部署完全一样；main 实例的容器名/目录
# 就是老布局，所以已有部署不需要搬迁。日志文件名是固定的（logs/fasttunnel-client-*.log），
# 所以日志目录必须按实例分开，否则两份写同一个文件、日切时还会互删。
#
# 首次部署先确认 docker 开机自启（否则机器重启后 daemon 不起，compose 里的 restart: always 也无从谈起）：
#   systemctl enable --now docker
# SELinux 为 enforcing 时（getenforce 查），docker-compose.yml 里那两个挂载末尾要加 :z。
#
# 客户端只往外连（连服务端 1270 + 连内网后台），本身不监听端口，所以客户端机的防火墙不必放行入站；
# host 网络下多实例也不抢端口。真正的主备要放不同物理机：同机两实例挂同一个后台时宿主一挂两份一起没。
#
# 改映射（Webs / 端口转发）不必重新出镜像：改完该实例的配置目录再跑一次本脚本（带上实例名）即可。
# ⚠ 数组是按下标合并的（见上线清单 §2.2）：覆盖文件里 Webs 写得比基线少，基线那条样例映射会照样注册出去。
set -e

APP=/opt/dotnet/fasttunnel-client
CONF=/opt/conf/fasttunnel-client
LOGS=/opt/logs/fasttunnel-client
PREFIX=fasttunnel-client
IMAGE=$PREFIX:latest
# CentOS 7 上可能是 docker-compose 1.x 独立版，也可能是 docker compose 插件
docker compose version >/dev/null 2>&1 && C='docker compose' || C='docker-compose'

usage() {
  sed -n 's/^# \{0,1\}//p' "$0" | sed -n '/一台机器上要多开几份/,/^$/p'
  echo "用法：sh $0 [--list|--all|--logs <实例>|--stop <实例>|<实例>]"
}

# 实例名 → 容器名 / project / 配置目录 / 日志目录，并导出给 compose 插值。
# 只有一处地方定义这套映射（另一处是 docker-compose.yml 里的默认值 = main 的形态）。
resolve() {
  INST="$1"
  case "$INST" in
    '' | *[!a-zA-Z0-9_-]*) echo "实例名只能用字母、数字、连字符、下划线：'$INST'"; exit 1 ;;
  esac
  if [ "$INST" = main ]; then
    CONTAINER="$PREFIX"; CFG_DIR="$CONF"; LOG_DIR="$LOGS"
  else
    CONTAINER="$PREFIX-$INST"; CFG_DIR="$CONF/$INST"; LOG_DIR="$LOGS/$INST"
  fi
  PROJ="$CONTAINER"
  CFG="$CFG_DIR/appsettings.Production.json"
  export FT_CONTAINER="$CONTAINER" FT_CONF_DIR="$CFG_DIR" FT_LOG_DIR="$LOG_DIR"
}

# 从配置文件里取出要转发的内网后端（Webs 与 TCP 型 Forwards 的 LocalIp:LocalPort，UDP 探不了就跳过）
backends_of() {
  awk '
    /"LocalIp"/    { ip = $0; sub(/.*"LocalIp"[[:space:]]*:[[:space:]]*"/, "", ip); sub(/".*/, "", ip) }
    /"LocalPort"/  { p  = $0; sub(/.*"LocalPort"[[:space:]]*:[[:space:]]*/, "", p); sub(/[^0-9].*/, "", p) }
    /"Protocol"/   { if ($0 ~ /UDP/) udp = 1 }
    /\}/           { if (ip != "" && p != "" && udp != 1) print ip, p; ip = ""; p = ""; udp = 0 }
    END            { if (ip != "" && p != "" && udp != 1) print ip, p }
  ' "$1"
}

guards() {
  # 单文件挂载：宿主这个文件必须已经存在，否则 Docker 会在挂载点建出一个同名目录，客户端读不到配置
  if [ ! -f "$CFG" ]; then
    echo "缺 $CFG（服务端地址 / Token / 内网映射都在里面，它不进镜像）"
    if [ "$INST" != main ] && [ -f "$CONF/appsettings.Production.json" ]; then
      echo "  新实例先建目录并从 main 拷一份再改成本机该有的映射："
      echo "  mkdir -p $CFG_DIR && cp $CONF/appsettings.Production.json $CFG"
    fi
    exit 1
  fi
  # 基线 appsettings.json 是一份全 change-me-* 的模板；覆盖文件还留着占位符的话，客户端会静默去连一个不存在的主机名
  PLACEHOLDER="$(grep -v '^[[:space:]]*//' "$CFG" | grep -c 'change-me-' || true)"
  [ "$PLACEHOLDER" -eq 0 ] || { echo "$CFG 里还有 $PLACEHOLDER 处 change-me-* 占位符，那是模板不是真实配置"; exit 1; }
  # 产物里不该有凭证（客户端 csproj 只拷 appsettings.json）；出现了就会被 COPY 烧进镜像层
  test ! -f appsettings.Production.json || { echo '产物里有 appsettings.Production.json，重新 publish 再传'; exit 1; }
}

# 起容器前探一遍本实例的后端可达性。多份部署最容易出的事故不是容器起不来，而是
# “这份到某个网段不通却照样注册了这个站点”：服务端会把死后端一起排进候选队列，
# 在它被降级之前仍可能轮到，访问者看到的是 502 或等满超时。
# 探测在宿主上跑：容器是 host 网络，与宿主同一个网络视图，探到的就是容器将看到的可达性。
check_backends() {
  BACKENDS="$(backends_of "$CFG")"
  if [ -z "$BACKENDS" ]; then
    echo "$CFG 里没有任何 LocalIp:LocalPort 映射，这个实例登录上去什么也不注册"
    exit 1
  fi
  NBACKENDS="$(printf '%s\n' "$BACKENDS" | grep -c .)"
  BAD="$(printf '%s\n' "$BACKENDS" | while read -r ip port; do
          timeout 3 bash -c "exec 3<>/dev/tcp/$ip/$port" 2>/dev/null || printf '  %s:%s\n' "$ip" "$port"
        done)"
  if [ -n "$BAD" ]; then
    echo "本机连不上这些内网后端（$CFG 注册的站点要靠它们）："
    printf '%s\n' "$BAD"
    echo "要么把不归本实例的映射删掉，要么确认后台机器/网段可达后加 FT_ALLOW_UNREACHABLE=1 重跑。"
    [ "${FT_ALLOW_UNREACHABLE:-0}" = 1 ] || exit 1
    echo '  已按 FT_ALLOW_UNREACHABLE=1 放行'
  else
    echo "内网后端 $NBACKENDS 个全部可达"
  fi
}

start_one() {
  mkdir -p "$LOG_DIR"
  echo "停止实例 $INST 的容器（$CONTAINER）"
  $C -p "$PROJ" down
  echo "启动实例 $INST（镜像 $IMAGE，配置 $CFG，日志 $LOG_DIR）"
  # 不带 --build：镜像由 ensure_image 统一负责，起容器只读当前 $IMAGE
  $C -p "$PROJ" up -d
  sleep 5
  echo "--- 启动日志（看有没有「控制通道已连上」与本站点注册名单；没连上就核 ServerAddr/ServerPort/Token 与服务端 1270 的连通性）---"
  docker logs --tail 50 "$CONTAINER"
}

# 镜像只建一次：本地已有就复用（多实例的第二、三份都走这一路），没有才 build；
# 同一轮里真建过一次就不再重入（--rebuild --all 也不会把 N 份都 build 一遍）。
# ⚠ 重传产物不会自动反映到镜像：那时要加 --rebuild（up 发现 tag 指向了新镜像 ID 会重建容器）。
ensure_image() {
  [ "$BUILT" = 1 ] && return 0
  if [ "$REBUILD" = 1 ]; then
    echo "按 --rebuild 重建镜像 $IMAGE"
  elif docker image inspect "$IMAGE" >/dev/null 2>&1; then
    echo "复用已有镜像 $IMAGE（重传过 dll 就加 --rebuild）"
    return 0
  else
    echo "本地没有 $IMAGE，从产物目录 $APP 首次构建"
  fi
  $C -p "$PROJ" build
  BUILT=1
}

# 本机已放好配置的实例：main 的配置在 $CONF 根，其余各占 $CONF/<实例名>/ 一个子目录。
# 只读目录不读容器，所以 --all 能把还没起过的新实例也带上。
instances() {
  [ -f "$CONF/appsettings.Production.json" ] && printf 'main\n'
  for d in "$CONF"/*/; do
    [ -d "$d" ] || continue
    name="$(basename "$d")"
    [ "$name" = main ] && continue
    [ -f "$d/appsettings.Production.json" ] && printf '%s\n' "$name"
  done
  return 0
}

list_all() {
  echo '--- 本机 fasttunnel-client 容器 ---'
  docker ps -a --filter "name=$PREFIX" --format '{{.Names}}\t{{.Status}}'
  echo '--- 已放好配置的实例 ---'
  FOUND="$(instances)"
  if [ -z "$FOUND" ]; then
    echo "  （无：$CONF 下没有 appsettings.Production.json）"
  else
    printf '%s\n' "$FOUND" | sed 's/^/  /'
  fi
  # main 的映射规则是“配置放 $CONF 根”，建了 $CONF/main 会变成没人读的孤儿配置
  [ -d "$CONF/main" ] && echo "  ⚠ $CONF/main 是无效目录：main 实例的配置请直接放 $CONF 根"
  return 0
}

cd "$APP"

CMD=deploy; INST=main; REBUILD=0; BUILT=0
while [ $# -gt 0 ]; do
  case "$1" in
    -h | --help) usage; exit 0 ;;
    --list) CMD=list ;;
    --all) CMD=all ;;
    --rebuild) REBUILD=1 ;;
    # --logs/--stop 后面跟实例名；CMD 存去掉前缀的名字，要与下面 case 的分支名一致
    --logs | --stop) CMD="${1#--}"; INST="${2:-main}"; shift ;;
    -*) echo "未知选项：$1"; usage; exit 1 ;;
    *) INST="$1" ;;
  esac
  shift
done

if [ "$CMD" = list ]; then
  list_all
  exit 0
fi

resolve "$INST"
case "$CMD" in
  logs) docker logs --tail 50 "$CONTAINER"; exit 0 ;;
  stop) echo "停止实例 $INST（$CONTAINER）"; $C -p "$PROJ" down; exit 0 ;;
  all)
    FOUND="$(instances)"
    [ -n "$FOUND" ] || { echo "$CONF 下没有任何实例配置，先按上面的 scp 放好 appsettings.Production.json"; exit 1; }
    for one in $FOUND; do
      resolve "$one"
      guards; check_backends
      ensure_image
      start_one
    done
    ;;
  deploy)
    guards; check_backends
    ensure_image
    start_one
    ;;
  *) echo "内部错误：未处理的子命令 '$CMD'"; exit 1 ;;
esac
