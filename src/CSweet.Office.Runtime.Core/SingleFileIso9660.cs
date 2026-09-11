namespace CSweet.Office.Runtime.Core;

/// <summary>Compatibility adapter preserving the existing Office artifact-media format.</summary>
public static class SingleFileIso9660
{
    public const int SectorSize = CSweet.Isolation.Artifacts.SingleFileIso9660.SectorSize;
    public const string ArtifactFileName = CSweet.Isolation.Artifacts.SingleFileIso9660.ArtifactFileName;
    public static Task WriteAsync(Stream artifact, long artifactLength, Stream output, CancellationToken cancellationToken = default) =>
        CSweet.Isolation.Artifacts.SingleFileIso9660.WriteAsync(artifact, artifactLength, output, cancellationToken);
    public static Task<bool> VerifyArtifactDigestAsync(string isoPath, string expectedDigest, CancellationToken cancellationToken = default) =>
        CSweet.Isolation.Artifacts.SingleFileIso9660.VerifyArtifactDigestAsync(isoPath, expectedDigest, cancellationToken);
}