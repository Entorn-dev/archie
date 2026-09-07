namespace Archie.Runner;

public sealed record ScannerRuntimeLimits(
    TimeSpan HandshakeTimeout,
    TimeSpan ExecutionTimeout,
    TimeSpan CancellationGracePeriod,
    int MaxProtocolMessageBytes,
    long MaxStandardOutputBytes,
    long MaxStandardErrorBytes,
    int MaxObservations,
    int MaxSourceOwnership = 200_000)
{
    public static ScannerRuntimeLimits Default { get; } = new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromSeconds(5),
        1024 * 1024,
        128L * 1024 * 1024,
        4L * 1024 * 1024,
        100_000);

    public static TimeSpan MaximumExecutionTimeout { get; } = TimeSpan.FromMinutes(60);
}
