// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using FastTunnel.Core.Config;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Server;

/// <summary>
/// 启动期的"凭证还是不是仓库默认值"自检。
///
/// 背景：交付配置 <c>config/appsettings.json</c> 随二进制目录走，历史上它的 Tokens /
/// Api.JWT.IssuerSigningKey / Api.Accounts 全是仓库里的示例值，而 <c>GetToken</c> 是
/// [AllowAnonymous]、CORS 是 AllowAnyOrigin、管理 API 与访客入口又共用同一批监听端口
/// —— 部署时忘了改，就等于把带已知口令的后台直接挂到公网上，且这类错误过去没有任何提示。
///
/// 策略：只告警、不阻断。把服务停下来不是这里的职责，运维按环境改配置后重启即可，
/// 但改之前日志里一定有一行醒目的 WRN，容器 stdout 也看得到。
/// 要改成硬阻断的话，把 Program.cs 里的调用处换成 throw 即可（不建议：会让升级即停服）。
/// </summary>
public static class StartupSafetyCheck
{
    /// <summary>
    /// 模板占位符标记：配置值里出现它们就说明还没按环境改。
    /// 仓库里先后用过好几套写法（REPLACE_ME_xxx / change-me / your-domain / XXX_FOR_YYY），
    /// 这里全部认，不把检测绑死在某一种字面上；漏认的代价比误报大，所以宁多勿少。
    /// </summary>
    public static readonly string[] PlaceholderMarkers =
    [
        "REPLACE_ME",
        "change-me",
        "changeme",
        "your-domain",
        "_FOR_",
        "placeholder",
        "example.com",
        "test.cc",
        "TODO",
        "FIXME",
    ];

    /// <summary>历史遗留的仓库默认签名密钥，逐个版本公开在 git 与镜像里。</summary>
    public const string DefaultIssuerSigningKey = "This is IssuerSigningKey";

    /// <summary>历史遗留的仓库默认管理账号口令。</summary>
    public const string DefaultAccountPassword = "admin123";

    /// <summary>历史遗留的仓库默认客户端 Token。</summary>
    public const string DefaultTunnelToken = "yimai@1688.com";

    /// <summary>签名密钥低于这个字节数就不该上生产（HMAC-SHA256 的实际强度上限就是密钥长度）。</summary>
    public const int MinSigningKeyBytes = 32;

    public static bool IsDefaultSigningKey(string? key)
        => string.Equals(key, DefaultIssuerSigningKey, StringComparison.Ordinal);

    /// <summary>
    /// 返回一份"人类可读的问题清单"，空集合表示配置在凭证这一项上是干净的。
    /// 只读不改，可单测。
    /// </summary>
    public static IReadOnlyList<string> FindIssues(DefaultServerConfig? option)
    {
        var issues = new List<string>();

        if (option == null)
        {
            issues.Add("配置里没有 FastTunnel 节，服务端将拿不到任何 Token / JWT 参数。");
            return issues;
        }

        // ---- 根域名：它是访客 Host 后缀匹配的根据，错了不是安全问题而是全站 404 ----
        if (string.IsNullOrWhiteSpace(option.WebDomain))
        {
            issues.Add("FastTunnel:WebDomain 为空：访客 Host 无法与任何站点匹配，所有隧道站点会直接 404。");
        }
        else if (ContainsPlaceholder(option.WebDomain))
        {
            issues.Add($"FastTunnel:WebDomain 仍是占位符“{option.WebDomain}”：除非客户端 Webs[].WWW 逐个列全域名，"
                + "否则 ${SubDomain}.${WebDomain} 这个入口全部对不上，访问者看到 404。");
        }

        // ---- 客户端登录凭证 ----
        if (option.Tokens == null || option.Tokens.Count == 0)
        {
            issues.Add("FastTunnel:Tokens 为空：任何不带 Token 的客户端都能登录（等于没有准入控制）。");
        }
        else
        {
            foreach (var token in option.Tokens)
            {
                if (ContainsPlaceholder(token))
                {
                    issues.Add($"FastTunnel:Tokens 里仍有占位符“{token}”，请按环境换成真实随机值，并让客户端配置同一个值。");
                }
                else if (string.Equals(token, DefaultTunnelToken, StringComparison.Ordinal))
                {
                    issues.Add("FastTunnel:Tokens 还是仓库历史默认值，必须更换（该值已公开在仓库与镜像里）。");
                }
                else if (token != null && token.Length < 16)
                {
                    issues.Add($"FastTunnel:Tokens 里有一个只有 {token.Length} 位的短值，抗不住在线猜测，建议 ≥ 32 位随机串。");
                }
            }
        }

        // ---- 管理端 JWT ----
        var jwt = option.Api?.JWT;
        if (jwt == null)
        {
            issues.Add("FastTunnel:Api:JWT 未配置：管理端接口无法签发/校验令牌。");
        }
        else if (ContainsPlaceholder(jwt.IssuerSigningKey))
        {
            issues.Add($"FastTunnel:Api:JWT:IssuerSigningKey 仍是占位符“{jwt.IssuerSigningKey}”，必须换成随机密钥（见生成方法）。");
        }
        else if (IsDefaultSigningKey(jwt.IssuerSigningKey))
        {
            issues.Add($"FastTunnel:Api:JWT:IssuerSigningKey 还是历史默认值“{DefaultIssuerSigningKey}”，任何人都能离线伪造管理端令牌。");
        }
        else if (System.Text.Encoding.UTF8.GetByteCount(jwt.IssuerSigningKey ?? "") < MinSigningKeyBytes)
        {
            issues.Add($"FastTunnel:Api:JWT:IssuerSigningKey 不足 {MinSigningKeyBytes} 字节，签名强度不够。");
        }

        // ---- 管理端账号 ----
        var accounts = option.Api?.Accounts;
        if (accounts != null)
        {
            foreach (var account in accounts)
            {
                if (account == null)
                {
                    continue;
                }

                if (ContainsPlaceholder(account.Password))
                {
                    issues.Add($"FastTunnel:Api:Accounts 里账号“{account.Name}”的口令仍是占位符，必须设置真实口令。");
                }
                else if (string.Equals(account.Password, DefaultAccountPassword, StringComparison.Ordinal))
                {
                    issues.Add($"FastTunnel:Api:Accounts 里账号“{account.Name}”的口令还是 {DefaultAccountPassword}，必须更换。");
                }
            }
        }

        // ---- 假开关：配置写了但代码里没有任何读取处 ----
        if (option.WebAllowAccessIps != null && option.WebAllowAccessIps.Length > 0)
        {
            issues.Add($"FastTunnel:WebAllowAccessIps 配了 {option.WebAllowAccessIps.Length} 项，但当前代码没有任何读取处，"
                + "它不会生效——不要把它当访问控制，请用宿主机防火墙 / 前置 Nginx 做来源限制。");
        }

        return issues;
    }

    private static bool ContainsPlaceholder(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var marker in PlaceholderMarkers)
        {
            if (value.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 打日志：有问题的用 Warning，没问题用 Information（让运维在日志里能确认"自检过了"）。
    /// </summary>
    public static int Log(ILogger logger, DefaultServerConfig? option)
    {
        var issues = FindIssues(option);

        if (issues.Count == 0)
        {
            logger.LogInformation("[SafetyCheck] 凭证自检通过：Tokens / JWT 签名密钥 / 管理端账号均非仓库默认值或占位符");
            return 0;
        }

        foreach (var issue in issues)
        {
            logger.LogWarning($"[SafetyCheck] {issue}");
        }

        logger.LogWarning($"[SafetyCheck] 共发现 {issues.Count} 项配置问题；"
            + "生成随机值：openssl rand -base64 48（Windows 可用 [Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 } | ForEach-Object { [byte]$_ }))）。"
            + "改完 config/appsettings.json 或 config/appsettings.Production.json 后必须重启服务端才生效。");

        return issues.Count;
    }
}
