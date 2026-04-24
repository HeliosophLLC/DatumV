using Tapper;

namespace DatumIngest.Web.Hubs;

/// <summary>
/// SignalR push payloads for
/// <see cref="DatumIngest.Models.Calibration.ICalibrationObserver"/>
/// ramp lifecycle events. Modelled as four separate records (started,
/// step, halted, completed) so the client can pattern-match on the
/// receiver method name, mirroring the engine's interface shape and
/// avoiding a discriminator field.
/// </summary>
[TranspilationSource]
public sealed record CalibrationRampStartedEvent(
    string ModelName,
    string Fingerprint);

[TranspilationSource]
public sealed record CalibrationRampStepEvent(
    string ModelName,
    int BatchSize,
    long TotalVramBytes,
    double DispatchMs);

/// <summary>
/// Mirror of <see cref="DatumIngest.Models.Calibration.HaltReason"/> in
/// the web DTO layer.
/// </summary>
[TranspilationSource]
public enum CalibrationHaltReason
{
    LookAheadProjection,
    DurationSpill,
    DispatchError,
}

[TranspilationSource]
public sealed record CalibrationRampHaltedEvent(
    string ModelName,
    int LastBatchSize,
    CalibrationHaltReason Reason);

[TranspilationSource]
public sealed record CalibrationRampCompletedEvent(
    string ModelName,
    int EntryCount);
