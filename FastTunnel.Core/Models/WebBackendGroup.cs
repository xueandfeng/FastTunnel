// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019-2022 Gui.H

using System.Collections.Generic;
using System.Linq;

namespace FastTunnel.Core.Models
{
    /// <summary>
    /// 一个站点域名对应的后端集合。
    /// 多个客户端实例（主备）登录同一站点时各自挂在这里：某条控制通道断开时，
    /// 请求会自动落到仍然存活的后端上，而不是把异常抛给访问者。
    /// </summary>
    public class WebBackendGroup
    {
        private readonly string domain;
        private readonly object gate = new object();
        private readonly List<WebInfo> backends = new List<WebInfo>();

        public WebBackendGroup(string domain)
        {
            this.domain = domain;
        }

        public string Domain => domain;

        public int Count
        {
            get
            {
                lock (gate)
                {
                    return backends.Count;
                }
            }
        }

        /// <summary>
        /// 注册后端。同一会话重复登录只保留最新一条，返回注册后的后端数。
        /// </summary>
        public int Add(WebInfo info)
        {
            lock (gate)
            {
                backends.RemoveAll(x => x.SessionId == info.SessionId);
                backends.Add(info);
                return backends.Count;
            }
        }

        /// <summary>
        /// 会话注销时摘掉它注册的后端。清空后保留空组，站点在列表里仍可见，便于判断
        /// "域名还在配置里但没有任何活着的前端"。
        /// </summary>
        public int RemoveSession(string sessionId)
        {
            lock (gate)
            {
                return backends.RemoveAll(x => x.SessionId == sessionId);
            }
        }

        /// <summary>
        /// 可选后端顺序：控制通道仍为 Open 的才有资格。没被降级过的优先，
        /// 同样健康时按 Priority 从大到小（客户端没配就都是 0），优先级也相等时最后注册者优先
        /// （与旧版“后登录覆盖路由”的行为一致）。
        /// 降级过的后端不摘除、只是排到最后：偶发慢握手不该把还在跑的后端打死，
        /// 只要它下一次建得上隧道就能凭 ResetTimeouts 回到队首。
        /// Priority 让位在 ConsecutiveTimeouts 之后是故意的：健康程度是事实，优先级是人的意见，
        /// 不能让“我是主机”把一个正在连续降级失败的后端顶到健康那台前面去。
        /// 同时把已断开却没能走到注销流程的悬空条目摘除，避免请求继续被送向死连接。
        /// </summary>
        public List<WebInfo> Candidates()
        {
            lock (gate)
            {
                backends.RemoveAll(x => !x.IsAlive);
                return backends.OrderBy(x => x.ConsecutiveTimeouts)
                    .ThenByDescending(x => x.Priority)
                    .ThenByDescending(x => x.RegisteredUtc).ToList();
            }
        }

        /// <summary>
        /// 控制通道已经关闭的后端立即摘除，下一个请求不必再试它一次。
        /// 只能给 “确认不可用”的调用方用：隧道超时不是证据（我们可能只是等急了），
        /// 而摘除是永久的（只有客户端重新登录才会 Add 回来），误摘会把单实例站点
        /// 变成永不自愈的离线页。因超时落选的后端走 ConsecutiveTimeouts 降级，不走到这里。
        /// </summary>
        public void Drop(WebInfo info)
        {
            lock (gate)
            {
                backends.Remove(info);
            }
        }

        /// <summary>
        /// 管理接口展示用的首个后端（可能已断开，仅用于展示配置）。排序跟 Candidates 一致，
        /// 不然面板上指的后端与实际接流量那个不是同一台，排障时会认错人。
        /// </summary>
        public WebInfo? First()
        {
            lock (gate)
            {
                return backends.OrderByDescending(x => x.Priority)
                    .ThenByDescending(x => x.RegisteredUtc).FirstOrDefault();
            }
        }

        /// <summary>
        /// 不排序的快照，日志与诊断用。
        /// </summary>
        public List<WebInfo> Snapshot()
        {
            lock (gate)
            {
                return backends.ToList();
            }
        }
    }
}
