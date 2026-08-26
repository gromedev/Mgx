namespace Mgx.Engine.Errors;

/// <summary>
/// What went wrong, as one vocabulary shared by retry, circuit breaking, pacing,
/// telemetry, and the errors cmdlets surface. Classification happens in
/// <see cref="MgxErrorClassifier"/>; what to do about a class happens in
/// <see cref="MgxErrorPolicy"/>. Nothing else in the codebase decides either question.
/// </summary>
public enum MgxErrorClass
{
    /// <summary>429: the service is pacing this workload. Retryable for every method.</summary>
    Throttle,

    /// <summary>500/502/503/504: the service failed and may not have processed the request.</summary>
    TransientServer,

    /// <summary>
    /// The request may never have arrived: a connection failure, a timeout (408 or a
    /// client-side attempt timeout), a reset mid-body.
    /// </summary>
    TransientTransport,

    /// <summary>401: the token is missing, expired, or not valid here.</summary>
    Authentication,

    /// <summary>403: authenticated, but not permitted.</summary>
    Authorization,

    /// <summary>
    /// Declared for the read-after-write window (a 404 immediately following a successful
    /// write). Nothing produces it yet; the consistency policy that will is future work,
    /// and telemetry and category mappings are complete for it now so adding the producer
    /// does not change any schema.
    /// </summary>
    Consistency,

    /// <summary>400 and kin: the request is malformed and will fail identically every time.</summary>
    InvalidRequest,

    /// <summary>404: the path is valid and the object is not there.</summary>
    NotFound,

    /// <summary>409/412: the object changed underneath the request.</summary>
    Conflict,

    /// <summary>Everything else, including cancellation: retrying cannot help.</summary>
    Permanent,
}
