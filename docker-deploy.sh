#!/usr/bin/env bash
# FastTunnel 容器部署脚本（在 Linux 服务器上执行）
#
# 用法：
#   ./docker-deploy.sh load /tmp/fasttunnel-server_2.3.0.tar   # 导入离线镜像
#   ./docker-deploy.sh server                                  # 启动/重建服务端容器
#   ./docker-deploy.sh client                                  # 启动/重建客户端容器
#   ./docker-deploy.sh diag                                    # 打印容器重启/OOM/会话诊断信息
#   ./docker-deploy.sh logs server                             # 跟踪日志
#   ./docker-deploy.sh logfiles server                         # 看文件日志
#   ./docker-deploy.sh dist server                             # 断线时长分布 + 原因分类
#   ./docker-deploy.sh stop server                             # 停止并删除容器
#
# 可用环境变量（可写在同目录 docker-deploy.env 里，会自动 source）：
#   IMAGE_TAG       镜像 tag，默认 2.3.0
#   REGISTRY        镜像前缀，默认 local
#   DATA_DIR        配置与日志持久化目录，默认 /srv/fasttunnel
#   SERVER_NETWORK  服务端网络模式，默认 host（穿透端口很多时最省事）；
#                   改成 bridge 时用 SERVER_PORTS 指定要映射的端口
#   SERVER_PORTS    bridge 模式下的端口，如 "-p 1270:1270 -p 8080:8080 -p 9000:9000"
#   CLIENT_APPSETTINGS 客户端配置文件路径，默认 $DATA_DIR/client-appsettings.json

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ -f "$SCRIPT_DIR/docker-deploy.env" ]]; then
    # shellcheck disable=SC1091
    source "$SCRIPT_DIR/docker-deploy.env"
fi

IMAGE_TAG="${IMAGE_TAG:-2.3.0}"
REGISTRY="${REGISTRY:-local}"
DATA_DIR="${DATA_DIR:-/srv/fasttunnel}"
SERVER_NETWORK="${SERVER_NETWORK:-host}"
SERVER_PORTS="${SERVER_PORTS:--p 1270:1270 -p 8080:8080}"
SERVER_IMAGE="$REGISTRY/fasttunnel-server:$IMAGE_TAG"
CLIENT_IMAGE="$REGISTRY/fasttunnel-client:$IMAGE_TAG"
CLIENT_APPSETTINGS="${CLIENT_APPSETTINGS:-$DATA_DIR/client-appsettings.json}"
SERVER_NAME=fasttunnel-server
CLIENT_NAME=fasttunnel-client

ACTION="${1:-help}"

log() { printf '\033[1;36m==>\033[0m %s\n' "$*"; }
die() { printf '\033[1;31m错误:\033[0m %s\n' "$*" >&2; exit 1; }

remove_if_exists() {
    if docker ps -a --format '{{.Names}}' | grep -qx "$1"; then
        log "移除已存在的容器 $1"
        docker rm -f "$1" >/dev/null
    fi
}

# 把 server/client 这种写法映射成容器名
container_of() {
    case "$1" in
        server) echo "$SERVER_NAME" ;;
        client) echo "$CLIENT_NAME" ;;
        *) die "未知目标：$1（应为 server 或 client）" ;;
    esac
}

run_server() {
    docker image inspect "$SERVER_IMAGE" >/dev/null 2>&1 \
        || die "本机没有镜像 $SERVER_IMAGE，先执行 docker load 或修改 IMAGE_TAG/REGISTRY"

    mkdir -p "$DATA_DIR/config" "$DATA_DIR/logs"

    # 2.3.0 里 FastTunnel:BinPort 已经没有任何代码读取，监听端口只由配置里的 urls 决定；
    # 而镜像内 start.sh 会把 $DATA_DIR/config/ 拷进容器覆盖同名文件，所以这里检查的就是真正生效的那份。
    # 缺 urls 会造成“容器起来了但端口不对”，所以启动前先检查一次。
    local srv_cfg="$DATA_DIR/config/appsettings.json"
    if [[ -f "$srv_cfg" ]]; then
        if ! grep -q '"urls"' "$srv_cfg"; then
            log "警告：$srv_cfg 里没有 urls，服务端会只监听默认 5000 端口，客户端与访问者都连不上"
            log "      请在该文件根节点加上：\"urls\": \"http://*:1270;http://*:8080\" 再 $0 restart server"
        fi
    else
        log "首次启动：用镜像自带的 config（已含 urls）；以后则以 $srv_cfg 为准（start.sh 会拷进容器覆盖同名文件）"
        log "提醒：镜像升级带来的新配置字段不会自动出现在你已有的 $srv_cfg 里，升级后需手工补上"
    fi

    remove_if_exists "$SERVER_NAME"

    local netargs=()
    if [[ "$SERVER_NETWORK" == "host" ]]; then
        netargs=(--network host)
        log "使用 host 网络（服务端监听端口直接对宿主机生效）"
    else
        # shellcheck disable=SC2206
        netargs=(${SERVER_PORTS})
        log "使用 bridge 网络，映射：${netargs[*]}"
    fi

    # 镜像内 start.sh 会把 /vols/config 下的文件拷进 /app/config（覆盖同名，源文件保留），
    # 所以把宿主机配置目录挂到 /vols/config 就能既持久化又让改动真正生效。
    # 注意：产物与镜像里都不含 appsettings.Production.json（csproj 已排除，凭证不进产物），
    # 首次部署要手工把该文件放到 $DATA_DIR/config/，漏放则服务端会用占位符启动并打 [SafetyCheck] WRN。
    docker run -d --name "$SERVER_NAME" \
        --restart unless-stopped \
        "${netargs[@]}" \
        -v "$DATA_DIR/config:/vols/config" \
        -v "$DATA_DIR/logs:/app/logs" \
        -e TZ=Asia/Shanghai \
        "$SERVER_IMAGE" >/dev/null

    log "服务端已启动，稍等 3 秒后打印首屏日志"
    sleep 3
    docker logs --tail 40 "$SERVER_NAME" || true
}

run_client() {
    docker image inspect "$CLIENT_IMAGE" >/dev/null 2>&1 \
        || die "本机没有镜像 $CLIENT_IMAGE，先执行 docker load 或修改 IMAGE_TAG/REGISTRY"

    [[ -f "$CLIENT_APPSETTINGS" ]] \
        || die "找不到客户端配置 $CLIENT_APPSETTINGS；可从仓库 FastTunnel.Client/appsettings.json 复制一份再改 ServerAddr/Token"

    mkdir -p "$DATA_DIR/logs/client"
    remove_if_exists "$CLIENT_NAME"

    docker run -d --name "$CLIENT_NAME" \
        --restart unless-stopped \
        -v "$CLIENT_APPSETTINGS:/app/appsettings.json:ro" \
        -v "$DATA_DIR/logs/client:/app/logs" \
        -e TZ=Asia/Shanghai \
        "$CLIENT_IMAGE" >/dev/null

    log "客户端已启动，稍等 3 秒后打印首屏日志"
    sleep 3
    docker logs --tail 40 "$CLIENT_NAME" || true
}

diag() {
    for name in "$SERVER_NAME" "$CLIENT_NAME"; do
        if ! docker ps -a --format '{{.Names}}' | grep -qx "$name"; then
            log "$name 不存在，跳过"
            continue
        fi

        echo "===== $name ====="
        # RestartCount / OOMKilled 用来判断“每小时掉线”是不是容器被重启或进程被杀
        docker inspect -f 'Status={{.State.Status}} StartedAt={{.State.StartedAt}} RestartCount={{.RestartCount}} OOMKilled={{.State.OOMKilled}} Error={{.State.Error}}' "$name"
        docker stats --no-stream --format 'CPU={{.CPUPerc}} MEM={{.MemUsage}}' "$name" || true
        echo "--- 最近的重连/会话结束日志（来自 docker logs，文件日志见 $0 logfiles）---"
        docker logs --tail 3000 "$name" 2>&1 \
            | grep -E '会话结束|控制通道|客户端连接|客户端关闭|主动终止|心跳|重连|Connection reset|WebSocket|Session|Snap' \
            | tail -n 25 || true
        echo
    done
}

# 容器内 /app/logs 在宿主机的位置
log_dir_of() {
    case "$1" in
        server) echo "$DATA_DIR/logs" ;;
        client) echo "$DATA_DIR/logs/client" ;;
        *) die "未知目标：$1（应为 server 或 client）" ;;
    esac
}

# 直接看文件日志（验证“日志确实落到文件里了”，比 docker logs 更接近你要取证的历史）
logfiles() {
    local which="${1:-server}"
    local dir; dir="$(log_dir_of "$which")"

    log "$which 日志目录：$dir"
    ls -lh "$dir" 2>/dev/null || die "看不到 $dir，确认容器已启动、DATA_DIR 正确"

    echo "--- $dir 最后 40 行 ---"
    tail -n 40 "$dir"/*.log 2>/dev/null || log "日志文件还是空的（可能没新日志或级别太高）"
}

# 断线分布统计：把每次会话结束的“时长 + 原因”抽出来，判断是随机还是固定阈值
dist() {
    local which="${1:-server}"
    local dir; dir="$(log_dir_of "$which")"

    ls "$dir"/*.log >/dev/null 2>&1 || die "找不到 $dir/*.log：先确认文件日志已开启（Serilog:WriteTo:File）且 DATA_DIR 正确"

    local rows; rows="$(mktemp)"
    # shellcheck disable=SC2064
    trap "rm -f '$rows'" RETURN

    grep -hE '会话结束|控制通道结束|客户端关闭' "$dir"/*.log 2>/dev/null | awk '
        # 用 index/length 而不是手算字节偏移：gawk 按字符、mawk 按字节，
        # 而 length() 与 substr() 始终本地一致，所以这种写法两边都对。
        function field_after(src, marker, delim,   i, rest, j) {
            i = index(src, marker); if (!i) return ""
            rest = substr(src, i + length(marker))
            j = index(rest, delim); if (j) rest = substr(rest, 1, j - 1)
            return rest
        }
        function dur2sec(tok,   n) {
            n = 0
            if (match(tok, /[0-9]+小时/)) n += substr(tok, RSTART, RLENGTH - length("小时")) * 3600
            if (match(tok, /[0-9]+分/)) n += substr(tok, RSTART, RLENGTH - length("分")) * 60
            if (match(tok, /[0-9]+秒/)) n += substr(tok, RSTART, RLENGTH - length("秒"))
            return n
        }
        {
            ts = substr($0, 1, 19)
            rsn = field_after($0, "结束：", "｜")
            if (rsn == "") rsn = field_after($0, "终止会话（", "）")
            # “客户端关闭”那种没有原因的重复行直接跳过，避免同一次掉线被统计两次
            if (rsn == "") next
            dur = field_after($0, "在线 ", "｜")
            secs = (dur ~ /^[0-9]/) ? dur2sec(dur) : -1
            printf "%s\t%d\t%s\n", ts, secs, rsn
        }' > "$rows" || true

    if [[ ! -s "$rows" ]]; then
        log "这段时间没有断线记录（好事），继续观察或用 logfiles 看原始日志"
        return 0
    fi

    echo "===== $which 断线明细（最近 40 次）：时间 | 在线时长 | 原因 ====="
    awk -F'\t' '$2 >= 0 { printf "%s | %6ds | %s\n", $1, $2, $3 }' "$rows" | tail -n 40

    echo
    echo "===== 断线原因分类计数（哪一类最多就是它）====="
    awk -F'\t' '{ print $3 }' "$rows" | sort | uniq -c | sort -rn | head -n 20

    echo
    echo "===== 在线时长分布（随机 → 链路抖动；集中在某个值 → 超时阈值）====="
    awk -F'\t' '$2 >= 0 { print $2 }' "$rows" | sort -n | awk '
        { a[NR] = $1; b[int($1 / 300) * 5]++ }
        END {
            if (NR == 0) { print "无可统计样本"; exit }
            printf "样本 %d 个｜最短 %ds｜中位 %ds｜最长 %ds\n", NR, a[1], a[int((NR + 1) / 2)], a[NR]
            for (k in b) printf "  %3d-%3d 分钟：%d 次\n", k, k + 5, b[k]
        }' | sort -t: -k1
}

# 主备与故障转移视图：配了多个客户端实例时用这个看到底有没有生效
ha() {
    local which="${1:-server}"
    local dir; dir="$(log_dir_of "$which")"

    ls "$dir"/*.log >/dev/null 2>&1 || die "找不到 $dir/*.log：先确认文件日志已开启（Serilog:WriteTo:File）且 DATA_DIR 正确"

    echo "===== 后端注册 / 降级 / 摘除 / 切换记录（最近 40 条）====="
    grep -hE '新增一个后端|注销站点后端|没有可用后端|次降级|控制通道已关闭|全部不可用|域名不合法' "$dir"/*.log 2>/dev/null | tail -n 40 \
        || log "没有任何后端相关记录：可当客户端还没登录过"

    echo
    echo "===== 最近的链路快照（站点数与后端数）====="
    grep -h '\[Snap\]' "$dir"/*.log 2>/dev/null | grep '在线' | tail -n 6 || true

    echo
    echo "===== 怎么读 ====="
    log "同一站点后端数 >= 2：任一客户端实例掉线，请求会自动走另一个，访问者无感"
    log "「第 N 次降级」：后端没坏，只是隧道握手超过了 WebBackendWaitSeconds；"
    log "             本次让给别的后端，该后端保留但排到最后，建上隧道就清零，不用管"
    log "降级满 WebBackendTimeoutTripCount 次：不是摘除，只是之后对它只等窗口的 1/N，"
    log "             免得真半开的链路把访问者拖满整个窗口；它仍在候补名单里"
    log "「控制通道已关闭，摘除该后端」：这才是真被移出候选，只能等客户端重连才会回来"
    log "出现「N 个后端全部不可用」：当时没有健康后端，访问者拿到的是离线页而不是 502；"
    log "             客户端恢复后下一个请求即自愈，不需要重启服务端"
    log "出现「域名不合法」：客户端 appsettings 的 SubDomain/WWW 写错了，该站点没注册上"
    log "默认每个请求都新建一条隧道（WebReuseTunnel=false）：客户端半开时第一发就能在"
    log "             握手窗口内切走（实测 3.04 秒降级），代价是每请求多建一条隧道（实测隧道建立次数 1:1 对请求数）；"
    log "             设 true 能把 2000 发只压到建几十条隧道，实测确实有小幅收益"
    log "             （并发 50、2000 发：复用 1406-1479 rps / p50 21-22 毫秒 vs 不复用 1138-1230 / 26-28 毫秒，"
    log "             约多两成吞吐、快 5 毫秒），"
    log "             但客户端一旦半开，池里每条死隧道各自坑掉一发，干等到响应超时才 504（实测 504@100.05 秒），"
    log "             确认链路不会半开才设 true；只看日志就能判：[Snap] 里「在办隧道 - 在办请求」就是池内空闲隧道数"
    log "访客上传默认上限 28.6 MiB（30,000,000 字节），超了直接 413 且请求不会进隧道；"
    log "             需要接收大文件或大表单时，设 FastTunnel:WebMaxRequestBodySizeMb（0=不覆盖，改完重启），"
    log "             实测设 100 后 29 MB / 50 MB 上传均能 200"
    log "后端全不可用时访客拿到的是 503（不是 200）：监控与重试策略可以直接用状态码判；"
    log "             域名对但站点未注册则是 404。注意这从 2.3.0 起变了，依赖「永远 200」的老客户端要先改"
    log "隧道正常但仍拿到 504（RequestTimedOut）：是内网后台连续 100 秒没吐任何字节（不是总耗时超了）；"
    log "             实测：每 10 秒吐一次、共 140 秒的响应默认配置就能过，憋 110 秒才回首包的必 504"
    log "             报表导出类接口要么调大 WebResponseTimeoutSeconds（改完重启服务端），要么改成边算边下发；"
    log "             调大它之外没有其他上限：服务端对「总时长」没有限制，持续喂字的响应会始终挂着"
    log "             注意若响应头已经发给访客后才被断开，访客拿到的可能是半路断流的 200（没有错误码），"
    log "             服务端日志对应的是 Yarp 的 ResponseBodyDestination，要一并列入告警关键字"
    log "日志级别会直接改变吞吐：交付配置默认 Default=Information，但已单独把 "
    log "             Serilog:MinimumLevel:Override:Yarp.ReverseProxy 设为 Warning（原因见下一行），不要删掉这一项"
    log "             YARP 在 Information 级下每转发一个请求写两条日志（Proxying 与 Received HTTP/1.1 response），"
    log "             而 Sink 是同步写，本机实测并发 50 下这能把隧道吞吐从 ≈1200 rps 压到 ≈500 rps（p50 26→81 毫秒）"
    log "             排障时临时把 Default 提到 Information 可以（能看到 YARP 的转发细节），"
    log "             但那种状态下测到的吞吐不具参考价值，用完改回并重启容器"
}

case "$ACTION" in
    load)
        [[ -n "${2:-}" ]] || die "用法：$0 load <镜像tar>"
        log "加载 $2"
        docker load -i "$2"
        docker images --filter 'reference=*fasttunnel-*' --format 'table {{.Repository}}\t{{.Tag}}\t{{.Size}}'
        ;;
    server) run_server ;;
    client) run_client ;;
    all) run_server; run_client ;;
    restart)
        [[ -n "${2:-}" ]] || die "用法：$0 restart <server|client>"
        NAME="$(container_of "$2")"
        [[ -n "$NAME" ]] || die "未知目标：$2"
        docker restart "$NAME" && docker logs --tail 20 "$NAME" || true
        ;;
    stop)
        [[ -n "${2:-}" ]] || die "用法：$0 stop <server|client>"
        NAME="$(container_of "$2")"
        [[ -n "$NAME" ]] || die "未知目标：$2"
        remove_if_exists "$NAME"
        ;;
    logs)
        NAME="$SERVER_NAME"
        [[ "${2:-}" == "client" ]] && NAME="$CLIENT_NAME"
        docker logs -f --tail 200 "$NAME"
        ;;
    logfiles) logfiles "${2:-server}" ;;
    dist) dist "${2:-server}" ;;
    ha) ha "${2:-server}" ;;
    diag) diag ;;
    help|*)
        cat <<'USAGE'
FastTunnel 容器部署脚本

  ./docker-deploy.sh load <tar>     导入离线镜像
  ./docker-deploy.sh server         启动/重建服务端
  ./docker-deploy.sh client         启动/重建客户端
  ./docker-deploy.sh all            两者都启动
  ./docker-deploy.sh diag           查看重启次数/OOM/会话结束日志
  ./docker-deploy.sh logs [client]  跟踪容器标准输出日志
  ./docker-deploy.sh logfiles [which]  查看落到文件里的日志（/app/logs 挂载目录）
  ./docker-deploy.sh dist [which]   断线统计：时长分布 + 原因分类（定位不定时重连用这个）
  ./docker-deploy.sh ha [which]     主备后端与故障转移记录（跑了多个客户端实例时用这个）
  ./docker-deploy.sh restart <which>
  ./docker-deploy.sh stop <which>

配置项（环境变量或 docker-deploy.env）：
  IMAGE_TAG REGISTRY DATA_DIR SERVER_NETWORK SERVER_PORTS CLIENT_APPSETTINGS
USAGE
        ;;
esac
