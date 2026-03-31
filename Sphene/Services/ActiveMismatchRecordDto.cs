using System.Text.Json.Serialization;
using System;
using System.Collections.Generic;

namespace Sphene.Services;

internal sealed class ActiveMismatchRecordDto
{
    [JsonPropertyName("uid")]
    public string Uid { get; set; } = string.Empty;

    [JsonPropertyName("gamePath")]
    public string GamePath { get; set; } = string.Empty;

    [JsonPropertyName("mismatchCount")]
    public int MismatchCount { get; set; }

    [JsonPropertyName("totalCheckCount")]
    public int TotalCheckCount { get; set; }

    [JsonPropertyName("firstSeen")]
    public DateTimeOffset FirstSeen { get; set; }

    [JsonPropertyName("lastSeen")]
    public DateTimeOffset LastSeen { get; set; }

    [JsonPropertyName("sources")]
    public List<string> Sources { get; set; } = [];

    [JsonPropertyName("objectKinds")]
    public List<string> ObjectKinds { get; set; } = [];
}
