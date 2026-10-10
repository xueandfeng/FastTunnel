// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Models
{
    public class WebConfig
    {
        /// <summary>
        /// 子域名
        /// </summary>
        public string SubDomain { get; set; }

        /// <summary>
        /// 本地IP
        /// </summary>
        public string LocalIp { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public int LocalPort { get; set; }

        /// <summary>
        /// 个人域名
        /// </summary>
        public string[] WWW { get; set; }

        /// <summary>
        /// 本站点的选路优先级（默认 0）。只在多个客户端实例把同一个站点都挂上来时起作用：
        /// 服务端先按它选主，大的优先；相等时保持原来的“后登录者优先”。所以不开这个配置（都留 0）
        /// 行为跟以前一样，不用改任何现有配置。
        /// 用它的原因是“后登录者优先”会把主备角色交给登录顺序：谁重连得晚谁自动变主，
        /// 哪台在跑业务成了一个随机结果（想固定主备就把主配 10、备配 0）。
        /// 优先级不跨健康状态：被降级过的后端仍排到健康后端之后。
        /// </summary>
        public int Priority { get; set; }
    }
}
