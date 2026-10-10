// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FastTunnel.Core.Extensions
{
    public static class ObjectExtensions
    {
        public static string ToJson<T>(this T message, JsonTypeInfo<T> jsonTypeInfo)
        {
            if (message == null)
            {
                return null;
            }

            return JsonSerializer.Serialize(message, jsonTypeInfo: jsonTypeInfo);
        }
    }
}
