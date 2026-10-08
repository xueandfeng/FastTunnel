// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;

namespace FastTunnel.Core.Config
{
    public class DefaultServerConfig : IServerConfig
    {
        public string WebDomain { get; set; }

        public string[] WebAllowAccessIps { get; set; }

        public bool EnableForward { get; set; }

        [Obsolete("由Tokens替换")]
        public string Token { get; set; }

        public List<string> Tokens { get; set; }

        public ApiOptions Api { get; set; }

        /// <summary>
        /// 控制通道空闲超时（秒），0 = 关闭。开启后服务端会主动断开长时间没有任何控制帧
        /// （含应用层心跳）的客户端，用于清理链路静默断开时残留的僵尸会话。
        /// 默认 0：避免误踢不会发应用层心跳的老版本客户端，升级到 2.3.0+ 客户端后再打开。
        /// </summary>
        public int ClientIdleTimeoutSeconds { get; set; }

        /// <summary>
        /// 链路健康快照日志间隔（秒）：每隔这么久往日志写一条 [Snap]，记录在线会话数、
        /// 在办请求与在办隧道数、每条会话的在线/空闲时长与最后帧类型，
        /// 用于定位“不定时重连”当时到底有多少连接、哪条空闲最久。
        /// 默认 60，0 = 关闭。粒度是 5 秒（配小于 5 的值也按 5 秒发）。
        /// </summary>
        public int DiagnosticsLogIntervalSeconds { get; set; } = 60;

        /// <summary>
        /// 单个后端的“反向隧道建立”等待上限（秒），0 = 不限时（完全跟随请求自身的取消）。
        /// 默认 10，与端口转发侧的 10s 对齐。
        /// 计时区间是从服务端发出 SwapMsg 到客户端把 PROXY 回拨到位，
        /// 只包含信令投递与隧道握手；内网后台的处理时间不在这个窗口里，
        /// 慢接口（报表、导出等）不会被它掐断。
        /// </summary>
        public int WebBackendWaitSeconds { get; set; } = 10;

        /// <summary>
        /// 同一个后端连续多少次建不了隧道之后，后续的请求对它只给缩短后的探测预算
        /// （完整窗口的 1/N，下限 1 秒），默认 3。它只影响等多久，不影响候补名单：
        /// 超时永不摘除后端，否则单实例部署下一次偶发慢握手就能把唯一后端摘掉，
        /// 站点变成离线页而且不重连不自愈。降级过的后端只是排到最后，建上隧道即清零。
        /// </summary>
        public int WebBackendTimeoutTripCount { get; set; } = 3;

        /// <summary>
        /// 站点隧道是否允许跨请求复用，默认 false（不复用，每个请求重新选后端、新建一条隧道）。
        /// 复用的收益有两层：一是少建隧道本身（本机实测 50 并发 2000 发，不复用要建 2000 条，
        /// 复用只建几十条），省掉每请求三条 TCP 的生命周期与 TIME_WAIT；二是在噪声（服务端日志）
        /// 排除后能读到的小幅吞吐与延迟收益：同窗长轮对比为复用 1406-1479 rps / p50 21-22 毫秒，
        /// 不复用 1138-1230 rps / p50 26-28 毫秒，约 +21% 吞吐、p50 省 5 毫秒；走 wss 还要多一次 TLS 握手。
        /// 代价是请求会被直接塞进旧隧道、绕过上面的多后端选路：后端一旦半开，
        /// 访问者要干等到响应超时（默认 100 秒）才拿到 504（实测 504@100.05 秒），
        /// 切换完全没机会参与；不复用时同一发 3.04 秒就在握手窗口内降级切走。
        /// 默认保持 false：那点吞吐好处换不上这个尾风险。
        /// </summary>
        public bool WebReuseTunnel { get; set; }

        /// <summary>
        /// 转发过程中两次 I/O 之间允许的最大静默（秒），0 = 沿用默认 100 秒。
        /// 跟 WebBackendWaitSeconds 不是一回事：那个只管隧道握手，这个管后台不出字的时长。
        /// 实测它卡的是“静默”而不是总时长：每 10 秒吐一次字节、共 140 秒的响应默认配置就能过；
        /// 而憋 110 秒才回首包的接口，100 秒整必被掐（504）。
        /// 报表、导出这类长时间不出字的接口要么调大它，要么改成边算边下发。
        /// 调大没有“更外层的兜底”：客户端隧道两端那个写死的 10 分钟 ReadTimeout 实测在转发
        /// 路径上不起作用（数据走异步读），调成 1800 就是真等 30 分钟；改完要重启服务端才生效。
        /// </summary>
        public int WebResponseTimeoutSeconds { get; set; }

        /// <summary>
        /// 访客 → 服务端 允许的最大请求体（MB），0 = 不覆盖，沿用 Kestrel 默认的 30,000,000 字节（约 28.6 MiB）。
        /// 不设定这个开关时，走隧道的上传只要超过 28.6 MiB 就会被服务端 Kestrel 直接 413，
        /// 请求根本不会进隧道（本机实测 28 MB 过、29 MB 413），而且旧版本没有任何配置能抬高它。
        /// 响应方向不受它限制（所以下载 200 MB 没问题）。它卡的是访客送进来的 HTTP 请求体，
        /// 隧道内部的 PROXY 回拨请求没有请求体，不受影响；改完需重启服务端。设成很大的值意味着
        /// 大图上传、表单附件会长时间占住一条隧道与两端缓冲，推高并发下的内存占用。
        /// </summary>
        public int WebMaxRequestBodySizeMb { get; set; }

        public class ApiOptions
        {
            public JWTOptions JWT { get; set; }

            public Account[] Accounts { get; set; }
        }

        public class JWTOptions
        {
            public int ClockSkew { get; set; }

            public string ValidAudience { get; set; }

            public string ValidIssuer { get; set; }

            public string IssuerSigningKey { get; set; }

            public int Expires { get; set; }
        }

        public class Account
        {
            public string Name { get; set; }

            public string Password { get; set; }
        }
    }
}
