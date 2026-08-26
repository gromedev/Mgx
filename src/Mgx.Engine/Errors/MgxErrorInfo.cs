namespace Mgx.Engine.Errors;

/// <summary>
/// The classified facts of a failure. Facts only - what to do with them
/// (retry, count against the breaker, surface) belongs to <see cref="MgxErrorPolicy"/>.
/// </summary>
/// <param name="Class">The failure class.</param>
/// <param name="StatusCode">HTTP status, or 0 when there was no response (transport, timeout, circuit).</param>
/// <param name="ErrorCode">Graph's error.code, when the body has been read. Retry-time
/// classification runs at ResponseHeadersRead and never has it.</param>
/// <param name="ServerRetryAfter">The server's Retry-After, unclamped. Policy clamps.</param>
public readonly record struct MgxErrorInfo(
    MgxErrorClass Class,
    int StatusCode,
    string? ErrorCode = null,
    TimeSpan? ServerRetryAfter = null);
